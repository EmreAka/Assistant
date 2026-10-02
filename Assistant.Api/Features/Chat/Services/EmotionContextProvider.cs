using System.Globalization;
using Assistant.Api.Domain.Configurations;
using Microsoft.Agents.AI;

namespace Assistant.Api.Features.Chat.Services;

// The assistant's current mood (see EMOTION_PLAN.md). The chat model only reads it; EmotionUpdateJob sets it.
public class EmotionContextProvider(
    long chatId,
    IEmotionService emotionService,
    EmotionOptions options,
    ILogger logger
) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new AIContext();
        }

        try
        {
            var state = await emotionService.GetByChatIdAsync(chatId, cancellationToken);
            if (state is null)
            {
                return new AIContext();
            }

            var why = string.IsNullOrWhiteSpace(state.Reason)
                ? string.Empty
                : $"{Environment.NewLine}Why: {state.Reason}";

            return new AIContext
            {
                Instructions = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Current mood: {state.Mood} (valence {state.Valence:0.00}, arousal {state.Arousal:0.00}){why}")
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Mood must never break a chat reply; continue without it.
            logger.LogWarning(ex, "Emotion context failed; continuing without mood. ChatId: {ChatId}", chatId);
            return new AIContext();
        }
    }
}
