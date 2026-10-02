using System.Globalization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;

namespace Assistant.Api.Features.Chat.Services;

public interface IEmotionAgentService
{
    /// <summary>Classifies how the assistant reacts to one chat turn. Never returns numbers; see EmotionReaction.ToDelta.</summary>
    Task<EmotionReaction> ReactAsync(
        string personality,
        AgentEmotionState currentMood,
        IReadOnlyList<string> userMemory,
        string userMessage,
        string assistantMessage,
        DateTime turnCreatedAtUtc,
        CancellationToken cancellationToken);
}

// Plain strings, validated in ToDelta, like MemoryDecision.Action: the model picks an event type and
// an intensity, and config decides how far that moves the mood.
public sealed record EmotionReaction(
    string EventType,
    string Intensity,
    string Mood,
    string Reason,
    EmotionFollowUp? FollowUp = null)
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

// A check-in the model suggests after the user mentions an upcoming event (EMOTION_PLAN.md step 7).
// LocalTime is a plain string, validated in ResolveRunAtLocal, since it is model output.
public sealed record EmotionFollowUp(
    string LocalTime,
    string Note)
{
    public const string LocalTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// Returns when the check-in should run, in local time, or null when LocalTime is unparsable, not
    /// after localNow, or more than MaxDaysAhead away. A time inside the quiet hours is moved to the
    /// end of the window.
    /// </summary>
    public DateTime? ResolveRunAtLocal(DateTime localNow, EmotionCheckInOptions options)
    {
        if (string.IsNullOrWhiteSpace(Note)
            || !DateTime.TryParseExact(LocalTime?.Trim(), LocalTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var runAt)
            || runAt <= localNow
            || runAt > localNow.AddDays(options.MaxDaysAhead))
        {
            return null;
        }

        var time = TimeOnly.FromDateTime(runAt);
        var start = options.QuietHoursStart;
        var end = options.QuietHoursEnd;

        if (start == end)
        {
            return runAt;
        }

        // A window like 23:00-09:00 runs past midnight: the late part ends the next morning.
        if (start > end)
        {
            if (time >= start)
            {
                return runAt.Date.AddDays(1).Add(end.ToTimeSpan());
            }

            return time < end ? runAt.Date.Add(end.ToTimeSpan()) : runAt;
        }

        return time >= start && time < end ? runAt.Date.Add(end.ToTimeSpan()) : runAt;
    }
}
