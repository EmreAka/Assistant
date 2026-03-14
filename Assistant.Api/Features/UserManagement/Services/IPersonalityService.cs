namespace Assistant.Api.Features.UserManagement.Services;

public interface IPersonalityService
{
    Task<string?> GetPersonalityTextAsync(long chatId, CancellationToken cancellationToken);
}
