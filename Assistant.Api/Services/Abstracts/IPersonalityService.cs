namespace Assistant.Api.Services.Abstracts;

public interface IPersonalityService
{
    Task<string?> GetPersonalityTextAsync(long chatId, CancellationToken cancellationToken);
}
