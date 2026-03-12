using Assistant.Api.Data;
using Assistant.Api.Domain.Entities;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Assistant.Api.Services.Concretes;

public class MemoryService(
    ApplicationDbContext dbContext,
    IEmbeddingService embeddingService,
    ILogger<MemoryService> logger
) : IMemoryService
{
    public async Task<IReadOnlyList<UserMemory>> GetActiveMemoriesAsync(long chatId, int take, CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        var nowUtc = DateTime.UtcNow;

        return await dbContext.UserMemories
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.Status == UserMemoryStatuses.Active)
            .Where(x => x.ExpiresAt == null || x.ExpiresAt > nowUtc)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemory>> SearchRelevantMemoriesAsync(
        long chatId,
        string query,
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        var normalizedQuery = MemoryNormalization.NormalizeContent(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return await GetActiveMemoriesAsync(chatId, take, cancellationToken);
        }

        try
        {
            await BackfillEmbeddingsAsync(chatId, cancellationToken);

            var queryEmbedding = await embeddingService.GenerateQueryEmbeddingAsync(normalizedQuery, cancellationToken);
            if (queryEmbedding is null)
            {
                return await GetActiveMemoriesAsync(chatId, take, cancellationToken);
            }

            var nowUtc = DateTime.UtcNow;

            var memories = await dbContext.UserMemories
                .AsNoTracking()
                .Where(x => x.TelegramUser.ChatId == chatId)
                .Where(x => x.Status == UserMemoryStatuses.Active)
                .Where(x => x.ExpiresAt == null || x.ExpiresAt > nowUtc)
                .Where(x => x.Embedding != null)
                .OrderBy(x => x.Embedding!.CosineDistance(queryEmbedding))
                .ThenByDescending(x => x.Importance)
                .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
                .Take(take)
                .ToListAsync(cancellationToken);

            return memories.Count > 0
                ? memories
                : await GetActiveMemoriesAsync(chatId, take, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Semantic search failed for ChatId: {ChatId}. Falling back to ranked active memories.", chatId);
            return await GetActiveMemoriesAsync(chatId, take, cancellationToken);
        }
    }

    public async Task BackfillEmbeddingsAsync(long chatId, CancellationToken cancellationToken)
    {
        var memories = await dbContext.UserMemories
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.Status == UserMemoryStatuses.Active)
            .Where(x => x.Embedding == null)
            .ToListAsync(cancellationToken);

        if (memories.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var memory in memories)
        {
            memory.Embedding = await embeddingService.GenerateDocumentEmbeddingAsync(memory.Content, memory.Category, cancellationToken);
            changed |= memory.Embedding is not null;
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> SaveMemoryAsync(
        long chatId,
        string content,
        string category,
        int importance,
        CancellationToken cancellationToken)
    {
        var normalizedContent = MemoryNormalization.NormalizeContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return false;
        }

        var userId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == null)
        {
            logger.LogWarning("Memory could not be saved because telegram user was not found. ChatId: {ChatId}", chatId);
            return false;
        }

        var normalizedCategory = MemoryNormalization.NormalizeCategory(category);
        var normalizedImportance = Math.Clamp(importance, 1, 10);
        var nowUtc = DateTime.UtcNow;

        var existingMemory = await dbContext.UserMemories
            .FirstOrDefaultAsync(
                x => x.TelegramUserId == userId.Value
                    && x.Category == normalizedCategory
                    && x.Content == normalizedContent,
                cancellationToken);

        if (existingMemory is not null)
        {
            existingMemory.Importance = Math.Max(existingMemory.Importance, normalizedImportance);
            existingMemory.LastUsedAt = nowUtc;
            existingMemory.Status = UserMemoryStatuses.Active;
            existingMemory.ArchivedAt = null;
            existingMemory.MergedIntoMemoryId = null;

            if (existingMemory.Embedding is null)
            {
                existingMemory.Embedding = await embeddingService.GenerateDocumentEmbeddingAsync(
                    normalizedContent,
                    normalizedCategory,
                    cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        Vector? embedding = null;
        try
        {
            embedding = await embeddingService.GenerateDocumentEmbeddingAsync(
                normalizedContent,
                normalizedCategory,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Memory embedding generation failed for ChatId: {ChatId}. Saving memory without embedding.", chatId);
        }

        dbContext.UserMemories.Add(new UserMemory
        {
            TelegramUserId = userId.Value,
            Category = normalizedCategory,
            Content = normalizedContent,
            Importance = normalizedImportance,
            Embedding = embedding,
            Status = UserMemoryStatuses.Active,
            CreatedAt = nowUtc,
            LastUsedAt = nowUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task TouchMemoriesAsync(IEnumerable<int> memoryIds, CancellationToken cancellationToken)
    {
        var ids = memoryIds
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
        {
            return;
        }

        var memories = await dbContext.UserMemories
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        if (memories.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var memory in memories)
        {
            memory.LastUsedAt = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
