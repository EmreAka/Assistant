using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
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
                .Select(x => new { x.UserMessage, x.AssistantMessage, x.TelegramUser.ChatId })
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
            var reaction = await agentService.ReactAsync(
                string.IsNullOrWhiteSpace(personality) ? PersonalityContextProvider.DefaultPersonalityText : personality,
                currentMood,
                turn.UserMessage,
                turn.AssistantMessage,
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mood update failed; keeping the current mood. TelegramUserId: {TelegramUserId}, TurnId: {TurnId}", telegramUserId, turnId);
        }
    }
}
