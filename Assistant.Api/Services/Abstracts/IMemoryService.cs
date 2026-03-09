using Assistant.Api.Domain.Entities;

namespace Assistant.Api.Services.Abstracts;

public interface IMemoryService
{
    Task<IReadOnlyList<UserMemory>> GetActiveMemoriesAsync(long chatId, int take, CancellationToken cancellationToken);
    Task<bool> SaveMemoryAsync(long chatId, string content, string category, int importance, CancellationToken cancellationToken);
    Task TouchMemoriesAsync(IEnumerable<int> memoryIds, CancellationToken cancellationToken);
}
