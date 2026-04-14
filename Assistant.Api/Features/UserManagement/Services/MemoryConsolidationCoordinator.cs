using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryConsolidationCoordinator(
    ApplicationDbContext dbContext,
    IMemoryConsolidationScheduler scheduler,
    IOptions<MemoryConsolidationOptions> options,
    ILogger<MemoryConsolidationCoordinator> logger
) : IMemoryConsolidationCoordinator
{
    private readonly MemoryConsolidationOptions _options = options.Value;

    public async Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken)
    {
        var state = await GetOrCreateStateAsync(telegramUserId, cancellationToken);
        if (state is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var staleBeforeUtc = now.AddMinutes(-_options.StaleJobAfterMinutes);
        var isFreshQueue = state.IsJobQueued
            && ((state.JobStartedAtUtc ?? state.JobQueuedAtUtc) is DateTime timestamp)
            && timestamp >= staleBeforeUtc;

        if (isFreshQueue)
        {
            return;
        }

        var unconsolidatedTurnCount = await dbContext.ChatTurns
            .AsNoTracking()
            .Where(x => x.TelegramUserId == telegramUserId && x.Id > state.LastConsolidatedChatTurnId)
            .CountAsync(cancellationToken);

        if (unconsolidatedTurnCount < _options.TurnsThreshold)
        {
            return;
        }

        state.IsJobQueued = true;
        state.JobQueuedAtUtc = now;
        state.JobStartedAtUtc = null;
        state.LastError = string.Empty;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var jobId = scheduler.Enqueue(telegramUserId);
            logger.LogInformation(
                "Queued memory consolidation job. TelegramUserId: {TelegramUserId}, JobId: {JobId}, PendingTurns: {PendingTurns}",
                telegramUserId,
                jobId,
                unconsolidatedTurnCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue memory consolidation job. TelegramUserId: {TelegramUserId}", telegramUserId);

            state.IsJobQueued = false;
            state.JobQueuedAtUtc = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<UserMemoryConsolidationState?> GetOrCreateStateAsync(
        int telegramUserId,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.UserMemoryConsolidationStates
            .FirstOrDefaultAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);

        if (state is not null)
        {
            return state;
        }

        var userExists = await dbContext.TelegramUsers
            .AsNoTracking()
            .AnyAsync(x => x.Id == telegramUserId, cancellationToken);

        if (!userExists)
        {
            logger.LogWarning(
                "Skipping memory consolidation queue check because telegram user was not found. TelegramUserId: {TelegramUserId}",
                telegramUserId);
            return null;
        }

        state = new UserMemoryConsolidationState
        {
            TelegramUserId = telegramUserId,
            LastError = string.Empty
        };

        dbContext.UserMemoryConsolidationStates.Add(state);
        await dbContext.SaveChangesAsync(cancellationToken);
        return state;
    }
}
