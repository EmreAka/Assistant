using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;

namespace Assistant.Api.Features.Chat.Services;

public interface IEmotionAgentService
{
    /// <summary>Classifies how the assistant reacts to one chat turn. Never returns numbers; see EmotionReaction.ToDelta.</summary>
    Task<EmotionReaction> ReactAsync(
        string personality,
        AgentEmotionState currentMood,
        string userMessage,
        string assistantMessage,
        CancellationToken cancellationToken);
}

// Plain strings, validated in ToDelta, like MemoryDecision.Action: the model picks an event type and
// an intensity, and config decides how far that moves the mood.
public sealed record EmotionReaction(
    string EventType,
    string Intensity,
    string Mood,
    string Reason)
{
    public const string FallbackEventType = "neutral";
    public const string FallbackIntensity = "low";

    /// <summary>
    /// Maps the reaction to a mood change: Events[EventType] * IntensityMultipliers[Intensity].
    /// Model output is untrusted, so an unknown event type falls back to neutral and an unknown
    /// intensity to low; IsValid is false then, for the caller to log. The per-turn cap is applied
    /// later by EmotionService.ApplyAsync.
    /// </summary>
    public (EmotionDelta Delta, bool IsValid) ToDelta(EmotionOptions options)
    {
        var knownEvent = options.Events.TryGetValue(EventType?.Trim() ?? string.Empty, out var effect);
        if (!knownEvent)
        {
            effect = options.Events.GetValueOrDefault(FallbackEventType) ?? new EmotionEventEffect();
        }

        var knownIntensity = options.IntensityMultipliers.TryGetValue(Intensity?.Trim() ?? string.Empty, out var multiplier);
        if (!knownIntensity)
        {
            multiplier = options.IntensityMultipliers.GetValueOrDefault(FallbackIntensity);
        }

        var delta = new EmotionDelta(
            effect!.Valence * multiplier,
            effect.Arousal * multiplier,
            Mood ?? string.Empty,
            Reason ?? string.Empty);

        return (delta, knownEvent && knownIntensity);
    }
}
