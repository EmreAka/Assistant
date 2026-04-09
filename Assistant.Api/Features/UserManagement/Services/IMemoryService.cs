using Assistant.Api.Features.UserManagement.Models;

namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryService
{
    Task<IReadOnlyList<UserMemory>> GetActiveMemoriesAsync(long chatId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserMemory>> GetRecentExpiredTimeBoundMemoriesAsync(long chatId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserMemory>> SearchActiveMemoriesAsync(long chatId, string? query, int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserMemory>> SearchRecentExpiredTimeBoundMemoriesAsync(long chatId, string? query, int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserMemory>> ListMemoriesAsync(long chatId, string statusFilter, string? searchText, int maxResults, CancellationToken cancellationToken);
    Task<bool> SaveMemoryAsync(long chatId, string content, string category, int importance, DateTime? expiresAtUtc, CancellationToken cancellationToken);
    Task<UserMemory?> UpdateMemoryAsync(long chatId, int memoryId, string? content, string? category, int? importance, DateTime? expiresAtUtc, bool clearExpiration, CancellationToken cancellationToken);
    Task<bool> DeleteMemoryAsync(long chatId, int memoryId, CancellationToken cancellationToken);
    Task TouchMemoriesAsync(IEnumerable<int> memoryIds, CancellationToken cancellationToken);
}
