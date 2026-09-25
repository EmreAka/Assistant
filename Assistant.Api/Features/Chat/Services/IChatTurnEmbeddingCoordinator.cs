namespace Assistant.Api.Features.Chat.Services;

public interface IChatTurnEmbeddingCoordinator
{
    Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken);
}
