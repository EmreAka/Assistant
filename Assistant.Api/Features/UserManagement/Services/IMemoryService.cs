using Assistant.Api.Features.UserManagement.Models;

namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryService
{
    Task<string> GetActiveManifestAsync(long chatId, CancellationToken cancellationToken);
    Task<bool> SaveManifestAsync(long chatId, string content, CancellationToken cancellationToken);
}
