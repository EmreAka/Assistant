using Pgvector;

namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryItemService
{
    Task<IReadOnlyList<MemoryItemSummary>> GetCoreItemsAsync(long chatId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryItemSummary>> GetActiveItemsAsync(long chatId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryItemSearchResult>> FindNeighborsAsync(int telegramUserId, Vector vector, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryItemSearchResult>> SearchAsync(long chatId, Vector queryVector, CancellationToken cancellationToken);
    Task<bool> HasAnyItemsAsync(int telegramUserId, CancellationToken cancellationToken);
}

public sealed record MemoryItemSummary(
    int Id,
    string Text,
    string Category,
    bool IsCore,
    DateTime UpdatedAt);

public sealed record MemoryItemSearchResult(
    int Id,
    string Text,
    string Category,
    bool IsCore,
    DateTime CreatedAt,
    double Distance);
