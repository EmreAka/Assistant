namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryExtractionCoordinator
{
    Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken);
}
