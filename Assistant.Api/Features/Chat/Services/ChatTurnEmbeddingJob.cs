using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

[AutomaticRetry(Attempts = 2)]
[DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
public class ChatTurnEmbeddingJob(
    ApplicationDbContext dbContext,
    IChatTurnEmbeddingService embeddingService,
    IBackgroundJobClient backgroundJobClient,
    IOptions<EmbeddingOptions> options,
    ILogger<ChatTurnEmbeddingJob> logger
)
{
    private readonly EmbeddingOptions _options = options.Value;

    public async Task ExecuteAsync(int telegramUserId)
    {
        // A NULL embedding means "not embedded yet", so the column itself is the work queue.
        var turns = await dbContext.ChatTurns
            .Where(x => x.TelegramUserId == telegramUserId && x.Embedding == null)
            .OrderBy(x => x.Id)
            .Take(_options.MaxTurnsPerRun)
            .ToListAsync();

        if (turns.Count == 0)
        {
            return;
        }

        // One API call per turn: multi-input requests are rejected by the OpenRouter ZDR guardrail.
        var embeddedCount = 0;
        foreach (var turn in turns)
        {
            try
            {
                turn.Embedding = await embeddingService.EmbedDocumentAsync(
                    $"User: {turn.UserMessage}\nAssistant: {turn.AssistantMessage}",
                    CancellationToken.None);
                embeddedCount++;
            }
            catch (Exception ex)
            {
                // Leave it NULL so the next run retries it; one bad turn must not block the rest.
                logger.LogWarning(ex, "Failed to embed chat turn. ChatTurnId: {ChatTurnId}", turn.Id);
            }
        }

        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Chat turn embedding completed. TelegramUserId: {TelegramUserId}, EmbeddedTurns: {EmbeddedTurns}, FailedTurns: {FailedTurns}",
            telegramUserId,
            embeddedCount,
            turns.Count - embeddedCount);

        // Only matters for backfill. Requiring progress (embeddedCount > 0) stops an endless
        // re-queue loop when every remaining turn keeps failing.
        var remainingCount = await dbContext.ChatTurns
            .CountAsync(x => x.TelegramUserId == telegramUserId && x.Embedding == null);

        if (embeddedCount > 0 && remainingCount >= _options.MaxTurnsPerRun)
        {
            backgroundJobClient.Enqueue<ChatTurnEmbeddingJob>(job => job.ExecuteAsync(telegramUserId));
        }
    }
}
