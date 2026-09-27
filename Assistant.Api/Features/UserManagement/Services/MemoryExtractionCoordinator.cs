using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryExtractionCoordinator(
    ApplicationDbContext dbContext,
    IMemoryItemService memoryItemService,
    IBackgroundJobClient backgroundJobClient,
    IOptions<MemoryItemOptions> options,
    ILogger<MemoryExtractionCoordinator> logger
) : IMemoryExtractionCoordinator
{
    private readonly MemoryItemOptions _options = options.Value;

    public async Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken)
    {
        var pendingTurnCount = await dbContext.ChatTurns
            .AsNoTracking()
            .CountAsync(x => x.TelegramUserId == telegramUserId && x.MemoryProcessedAt == null, cancellationToken);

        // A user with a manifest but no memory items yet needs the one-time import right away,
        // otherwise the chat has no memory until TurnsThreshold turns pile up.
        var needsImport = pendingTurnCount < _options.TurnsThreshold
            && !await memoryItemService.HasAnyItemsAsync(telegramUserId, cancellationToken)
            && await dbContext.UserMemoryManifests
                .AsNoTracking()
                .AnyAsync(x => x.TelegramUserId == telegramUserId && x.IsActive, cancellationToken);

        if (pendingTurnCount < _options.TurnsThreshold && !needsImport)
        {
            return;
        }

        // No "already queued" flag: a duplicate job is harmless because the job is serialized
        // and only picks up turns that are still NULL.
        var jobId = backgroundJobClient.Enqueue<MemoryExtractionJob>(job => job.ExecuteAsync(telegramUserId));
        logger.LogInformation(
            "Queued memory extraction job. TelegramUserId: {TelegramUserId}, JobId: {JobId}, PendingTurns: {PendingTurns}, NeedsImport: {NeedsImport}",
            telegramUserId,
            jobId,
            pendingTurnCount,
            needsImport);
    }
}
