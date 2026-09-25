using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class ChatTurnEmbeddingCoordinator(
    ApplicationDbContext dbContext,
    IBackgroundJobClient backgroundJobClient,
    IOptions<EmbeddingOptions> options,
    ILogger<ChatTurnEmbeddingCoordinator> logger
) : IChatTurnEmbeddingCoordinator
{
    private readonly EmbeddingOptions _options = options.Value;

    public async Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken)
    {
        var pendingTurnCount = await dbContext.ChatTurns
            .AsNoTracking()
            .CountAsync(x => x.TelegramUserId == telegramUserId && x.Embedding == null, cancellationToken);

        if (pendingTurnCount < _options.TurnsThreshold)
        {
            return;
        }

        // No "already queued" flag: a duplicate job is harmless because the job is serialized
        // and only picks up turns that are still NULL.
        var jobId = backgroundJobClient.Enqueue<ChatTurnEmbeddingJob>(job => job.ExecuteAsync(telegramUserId));
        logger.LogInformation(
            "Queued chat turn embedding job. TelegramUserId: {TelegramUserId}, JobId: {JobId}, PendingTurns: {PendingTurns}",
            telegramUserId,
            jobId,
            pendingTurnCount);
    }
}
