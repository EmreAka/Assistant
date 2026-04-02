using Assistant.Api.Features.UserManagement.Models;

namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryService
{
    Task<IReadOnlyList<UserMemory>> GetActiveMemoriesAsync(long chatId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserMemory>> GetRecentExpiredTimeBoundMemoriesAsync(long chatId, CancellationToken cancellationToken);
    Task<bool> SaveMemoryAsync(long chatId, string content, string category, int importance, DateTime? expiresAtUtc, CancellationToken cancellationToken);
    Task TouchMemoriesAsync(IEnumerable<int> memoryIds, CancellationToken cancellationToken);
}
