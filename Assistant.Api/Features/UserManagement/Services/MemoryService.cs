using Assistant.Api.Data;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryService(
    ApplicationDbContext dbContext,
    ILogger<MemoryService> logger
) : IMemoryService
{
    private const int ExpiredTimeBoundMemoryLookbackDays = 30;
    private const int ExpiredTimeBoundMemoryLimit = 10;

    public async Task<IReadOnlyList<UserMemory>> GetActiveMemoriesAsync(long chatId, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        return await dbContext.UserMemories
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.ExpiresAt == null || x.ExpiresAt > nowUtc)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemory>> GetRecentExpiredTimeBoundMemoriesAsync(long chatId, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var expiredTimeBoundCutoffUtc = nowUtc.AddDays(-ExpiredTimeBoundMemoryLookbackDays);

        return await dbContext.UserMemories
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.ExpiresAt != null && x.ExpiresAt <= nowUtc && x.ExpiresAt >= expiredTimeBoundCutoffUtc)
            .OrderByDescending(x => x.ExpiresAt)
            .ThenByDescending(x => x.Importance)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .Take(ExpiredTimeBoundMemoryLimit)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> SaveMemoryAsync(
        long chatId,
        string content,
        string category,
        int importance,
        DateTime? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var normalizedContent = NormalizeContent(content);
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

        var normalizedCategory = NormalizeCategory(category);
        var normalizedImportance = Math.Clamp(importance, 1, 10);

        var existingMemory = await dbContext.UserMemories
            .FirstOrDefaultAsync(
                x => x.TelegramUserId == userId.Value
                    && x.Category == normalizedCategory
                    && x.Content == normalizedContent,
                cancellationToken);

        if (existingMemory is not null)
        {
            existingMemory.Importance = Math.Max(existingMemory.Importance, normalizedImportance);
            existingMemory.LastUsedAt = DateTime.UtcNow;
            if (expiresAtUtc.HasValue)
            {
                existingMemory.ExpiresAt = expiresAtUtc.Value;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        dbContext.UserMemories.Add(new UserMemory
        {
            TelegramUserId = userId.Value,
            Category = normalizedCategory,
            Content = normalizedContent,
            Importance = normalizedImportance,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = expiresAtUtc
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

    private static string NormalizeContent(string content)
    {
        return string.Join(
            " ",
            content
                .Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeCategory(string category)
    {
        var trimmed = category.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(trimmed) ? "fact" : trimmed;
    }
}
