namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryConsolidationCoordinator
{
    Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken);
}
