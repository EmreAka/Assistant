using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.UserManagement.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

// A mood update isn't worth replaying: failures are logged and dropped, and Hangfire never retries.
[AutomaticRetry(Attempts = 0)]
public class EmotionUpdateJob(
    ApplicationDbContext dbContext,
    IEmotionService emotionService,
    IEmotionAgentService agentService,
    IPersonalityService personalityService,
    IMemoryItemService memoryItemService,
    IDeferredIntentScheduler deferredIntentScheduler,
    IAssistantTimeService assistantTimeService,
    IOptions<EmotionOptions> options,
    ILogger<EmotionUpdateJob> logger
)
{
    private readonly EmotionOptions _options = options.Value;

    public async Task ExecuteAsync(int telegramUserId, int turnId)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            var turn = await dbContext.ChatTurns
                .AsNoTracking()
                .Where(x => x.Id == turnId && x.TelegramUserId == telegramUserId)
                .Select(x => new { x.UserMessage, x.AssistantMessage, x.CreatedAt, x.TelegramUser.ChatId })
                .FirstOrDefaultAsync();

            if (turn is null)
            {
                logger.LogWarning("Chat turn for mood update not found. TelegramUserId: {TelegramUserId}, TurnId: {TurnId}", telegramUserId, turnId);
                return;
            }

            // Skips the model call for a turn that a newer update already covered; ApplyAsync checks
            // again in the database for races.
            var currentMood = await emotionService.GetAsync(telegramUserId);
            if (currentMood.LastTurnId >= turnId)
            {
                return;
            }

            var personality = await personalityService.GetPersonalityTextAsync(turn.ChatId, CancellationToken.None);

            // Core memory items tell the mood model why something in the turn matters to the user.
            // Without them the update still runs, just with less context.
            IReadOnlyList<string> userMemory;
            try
            {
                userMemory = (await memoryItemService.GetCoreItemsAsync(turn.ChatId, CancellationToken.None))
                    .Select(x => x.Text)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Memory items for mood update could not be loaded; continuing without them. TelegramUserId: {TelegramUserId}", telegramUserId);
                userMemory = [];
            }

            var reaction = await agentService.ReactAsync(
                string.IsNullOrWhiteSpace(personality) ? PersonalityContextProvider.DefaultPersonalityText : personality,
                currentMood,
                userMemory,
                turn.UserMessage,
                turn.AssistantMessage,
                turn.CreatedAt,
                CancellationToken.None);

            var (delta, isValid) = reaction.ToDelta(_options);
            if (!isValid)
            {
                logger.LogWarning(
                    "Unknown mood reaction from the model; using {FallbackEventType}/{FallbackIntensity} instead. EventType: {EventType}, Intensity: {Intensity}",
                    EmotionReaction.FallbackEventType,
                    EmotionReaction.FallbackIntensity,
                    reaction.EventType,
                    reaction.Intensity);
            }

            var applied = await emotionService.ApplyAsync(telegramUserId, turnId, delta);

            logger.LogInformation(
                "Mood update {Result}. TelegramUserId: {TelegramUserId}, TurnId: {TurnId}, EventType: {EventType}, Intensity: {Intensity}, Mood: {Mood}",
                applied ? "applied" : "skipped",
                telegramUserId,
                turnId,
                reaction.EventType,
                reaction.Intensity,
                reaction.Mood);

            if (_options.CheckIns.Enabled && reaction.FollowUp is not null)
            {
                await ScheduleCheckInAsync(turn.ChatId, reaction.FollowUp);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mood update failed; keeping the current mood. TelegramUserId: {TelegramUserId}, TurnId: {TurnId}", telegramUserId, turnId);
        }
    }

    // Runs after the mood is saved, so a failure here never undoes the mood update.
    private async Task ScheduleCheckInAsync(long chatId, EmotionFollowUp followUp)
    {
        try
        {
            var runAtLocal = followUp.ResolveRunAtLocal(assistantTimeService.GetLocalNow(), _options.CheckIns);
            if (runAtLocal is null)
            {
                logger.LogWarning("Ignoring invalid check-in from the mood model. ChatId: {ChatId}, LocalTime: {LocalTime}, Note: {Note}", chatId, followUp.LocalTime, followUp.Note);
                return;
            }

            // At most one pending check-in per chat, so a chatty day doesn't turn into a stream of them.
            var hasPendingCheckIn = await dbContext.DeferredIntents
                .AnyAsync(x => x.ChatId == chatId
                    && x.Origin == DeferredIntentOrigins.Self
                    && (x.Status == DeferredIntentStatuses.Pending || x.Status == DeferredIntentStatuses.Scheduled));
            if (hasPendingCheckIn)
            {
                logger.LogInformation("Check-in skipped; one is already pending. ChatId: {ChatId}, Note: {Note}", chatId, followUp.Note);
                return;
            }

            // Same flow as TaskToolFunctions.ScheduleTask for a one-time task.
            var runAtUtc = assistantTimeService.ConvertLocalToUtc(runAtLocal.Value);
            var intent = new DeferredIntent
            {
                IntentId = Guid.NewGuid(),
                ChatId = chatId,
                OriginalInstruction = $"Check in with the user about: {followUp.Note.Trim()}",
                TimeZoneId = assistantTimeService.DefaultTimeZoneId,
                ScheduledAtUtc = runAtUtc,
                Status = DeferredIntentStatuses.Pending,
                Origin = DeferredIntentOrigins.Self
            };
            dbContext.DeferredIntents.Add(intent);
            await dbContext.SaveChangesAsync();

            intent.HangfireJobId = deferredIntentScheduler.ScheduleOneTime(intent.IntentId, runAtUtc);
            intent.Status = DeferredIntentStatuses.Scheduled;
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Check-in scheduled. ChatId: {ChatId}, IntentId: {IntentId}, RunAtLocal: {RunAtLocal}, Note: {Note}", chatId, intent.IntentId, runAtLocal, followUp.Note);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Check-in scheduling failed; the mood update is kept. ChatId: {ChatId}", chatId);
        }
    }
}
