using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.UserManagement.Models;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.UserManagement.Services;

[AutomaticRetry(Attempts = 2)]
public class MemoryConsolidationJob(
    ApplicationDbContext dbContext,
    IMemoryService memoryService,
    IMemoryConsolidationAgentService agentService,
    IMemoryConsolidationCoordinator coordinator,
    IOptions<MemoryConsolidationOptions> options,
    ILogger<MemoryConsolidationJob> logger
)
{
    private readonly MemoryConsolidationOptions _options = options.Value;

    public async Task ExecuteAsync(int telegramUserId)
    {
        var state = await dbContext.UserMemoryConsolidationStates
            .Include(x => x.TelegramUser)
            .FirstOrDefaultAsync(x => x.TelegramUserId == telegramUserId);

        if (state is null)
        {
            logger.LogWarning("Memory consolidation state was not found. TelegramUserId: {TelegramUserId}", telegramUserId);
            return;
        }

        var now = DateTime.UtcNow;
        var staleBeforeUtc = now.AddMinutes(-_options.StaleJobAfterMinutes);
        if (!state.IsJobQueued)
        {
            logger.LogInformation("Skipping memory consolidation because no queued work exists. TelegramUserId: {TelegramUserId}", telegramUserId);
            return;
        }

        if (state.JobStartedAtUtc.HasValue && state.JobStartedAtUtc.Value >= staleBeforeUtc)
        {
            logger.LogInformation("Skipping memory consolidation because another job is already running. TelegramUserId: {TelegramUserId}", telegramUserId);
            return;
        }

        state.JobStartedAtUtc = now;
        state.LastAttemptedAtUtc = now;
        state.LastError = string.Empty;
        await dbContext.SaveChangesAsync();

        try
        {
            var maxTurnIdToProcess = await dbContext.ChatTurns
                .AsNoTracking()
                .Where(x => x.TelegramUserId == telegramUserId)
                .Select(x => (int?)x.Id)
                .MaxAsync();

            if (maxTurnIdToProcess is null || maxTurnIdToProcess.Value <= state.LastConsolidatedChatTurnId)
            {
                await ClearQueueAsync(state, completedAtUtc: null);
                return;
            }

            var turns = await dbContext.ChatTurns
                .AsNoTracking()
                .Where(x => x.TelegramUserId == telegramUserId)
                .Where(x => x.Id > state.LastConsolidatedChatTurnId && x.Id <= maxTurnIdToProcess.Value)
                .OrderBy(x => x.Id)
                .Select(x => new MemoryConsolidationTurn(
                    x.UserMessage,
                    x.AssistantMessage,
                    x.CreatedAt))
                .ToListAsync();

            if (turns.Count < _options.TurnsThreshold)
            {
                logger.LogInformation(
                    "Skipping memory consolidation because pending turns are below threshold. TelegramUserId: {TelegramUserId}, PendingTurns: {PendingTurns}",
                    telegramUserId,
                    turns.Count);
                await ClearQueueAsync(state, completedAtUtc: null);
                return;
            }

            var chatId = state.TelegramUser.ChatId;
            var currentManifest = await memoryService.GetActiveManifestAsync(chatId, CancellationToken.None);
            var updatedManifest = await agentService.ConsolidateAsync(
                new MemoryConsolidationRequest(chatId, currentManifest, turns),
                CancellationToken.None);

            var normalizedCurrentManifest = NormalizeManifest(currentManifest);
            var normalizedUpdatedManifest = NormalizeManifest(updatedManifest);
            var shouldSaveNewVersion = !string.IsNullOrWhiteSpace(normalizedUpdatedManifest)
                && !string.Equals(normalizedCurrentManifest, normalizedUpdatedManifest, StringComparison.Ordinal);

            if (shouldSaveNewVersion)
            {
                var saved = await memoryService.SaveManifestAsync(chatId, normalizedUpdatedManifest, CancellationToken.None);
                if (!saved)
                {
                    throw new InvalidOperationException($"Failed to save updated manifest for chat {chatId}.");
                }
            }

            state.LastConsolidatedChatTurnId = maxTurnIdToProcess.Value;
            await ClearQueueAsync(state, DateTime.UtcNow);

            logger.LogInformation(
                "Memory consolidation completed. TelegramUserId: {TelegramUserId}, ProcessedTurnCount: {ProcessedTurnCount}, SavedNewVersion: {SavedNewVersion}",
                telegramUserId,
                turns.Count,
                shouldSaveNewVersion);

            try
            {
                await coordinator.QueueIfNeededAsync(telegramUserId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Follow-up memory consolidation queue check failed. TelegramUserId: {TelegramUserId}", telegramUserId);
            }
        }
        catch (Exception ex)
        {
            state.IsJobQueued = true;
            state.JobStartedAtUtc = null;
            state.LastError = ex.Message;
            await dbContext.SaveChangesAsync();

            logger.LogError(ex, "Memory consolidation failed. TelegramUserId: {TelegramUserId}", telegramUserId);
            throw;
        }
    }

    private async Task ClearQueueAsync(
        UserMemoryConsolidationState state,
        DateTime? completedAtUtc)
    {
        state.IsJobQueued = false;
        state.JobQueuedAtUtc = null;
        state.JobStartedAtUtc = null;
        state.LastCompletedAtUtc = completedAtUtc;
        state.LastError = string.Empty;
        await dbContext.SaveChangesAsync();
    }

    private static string NormalizeManifest(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            manifest
                .Trim()
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}
