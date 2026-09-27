using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Assistant.Api.Features.UserManagement.Services;

// All pgvector queries for memory items live here so jobs and providers can be tested with a fake
// (the InMemory provider can't run CosineDistance).
public class MemoryItemService(
    ApplicationDbContext dbContext,
    IOptions<MemoryItemOptions> options,
    ILogger<MemoryItemService> logger
) : IMemoryItemService
{
    private readonly MemoryItemOptions _options = options.Value;

    public async Task<IReadOnlyList<MemoryItemSummary>> GetCoreItemsAsync(long chatId, CancellationToken cancellationToken)
    {
        return await dbContext.UserMemoryItems
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId && x.Status == UserMemoryItemStatuses.Active && x.IsCore)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.Id)
            .Take(_options.MaxCoreItems)
            .Select(x => new MemoryItemSummary(x.Id, x.Text, x.Category, x.IsCore, x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryItemSummary>> GetActiveItemsAsync(long chatId, CancellationToken cancellationToken)
    {
        return await dbContext.UserMemoryItems
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId && x.Status == UserMemoryItemStatuses.Active)
            .OrderByDescending(x => x.IsCore)
            .ThenBy(x => x.Category)
            .ThenBy(x => x.Id)
            .Select(x => new MemoryItemSummary(x.Id, x.Text, x.Category, x.IsCore, x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    // Nearest active items (core included) for reconciling a candidate fact.
    public async Task<IReadOnlyList<MemoryItemSearchResult>> FindNeighborsAsync(
        int telegramUserId,
        Vector vector,
        CancellationToken cancellationToken)
    {
        var results = await QueryNearestAsync(
            dbContext.UserMemoryItems.Where(x => x.TelegramUserId == telegramUserId),
            vector,
            _options.ReconcileTopK,
            _options.ReconcileMaxCosineDistance,
            cancellationToken);

        LogDistances("Memory item neighbors", results);
        return results;
    }

    // Relevant non-core items for the current message; core items are always in the prompt already.
    public async Task<IReadOnlyList<MemoryItemSearchResult>> SearchAsync(
        long chatId,
        Vector queryVector,
        CancellationToken cancellationToken)
    {
        var results = await QueryNearestAsync(
            dbContext.UserMemoryItems.Where(x => x.TelegramUser.ChatId == chatId && !x.IsCore),
            queryVector,
            _options.RetrievalTopK,
            _options.RetrievalMaxCosineDistance,
            cancellationToken);

        LogDistances("Memory item search", results);
        return results;
    }

    public Task<bool> HasAnyItemsAsync(int telegramUserId, CancellationToken cancellationToken)
    {
        return dbContext.UserMemoryItems
            .AsNoTracking()
            .AnyAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);
    }

    // Vector search always returns the "closest" items, even when nothing is related,
    // so hits beyond maxDistance are dropped.
    private static async Task<IReadOnlyList<MemoryItemSearchResult>> QueryNearestAsync(
        IQueryable<UserMemoryItem> items,
        Vector vector,
        int topK,
        double maxDistance,
        CancellationToken cancellationToken)
    {
        return await items
            .AsNoTracking()
            .Where(x => x.Status == UserMemoryItemStatuses.Active)
            .Select(x => new
            {
                x.Id,
                x.Text,
                x.Category,
                x.IsCore,
                x.CreatedAt,
                Distance = x.Embedding.CosineDistance(vector)
            })
            .Where(x => x.Distance <= maxDistance)
            .OrderBy(x => x.Distance)
            .Take(topK)
            .Select(x => new MemoryItemSearchResult(x.Id, x.Text, x.Category, x.IsCore, x.CreatedAt, x.Distance))
            .ToListAsync(cancellationToken);
    }

    // Tuning aid for the distance cutoffs. Enable with a Debug log level for this class.
    private void LogDistances(string operation, IReadOnlyList<MemoryItemSearchResult> results)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        logger.LogDebug(
            "{Operation}. ResultCount: {ResultCount}, Hits: {Hits}",
            operation,
            results.Count,
            string.Join(", ", results.Select(x => $"{x.Id}:{x.Distance:F3}")));
    }
}
