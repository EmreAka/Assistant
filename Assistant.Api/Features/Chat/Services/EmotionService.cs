using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class EmotionService(
    ApplicationDbContext dbContext,
    IOptions<EmotionOptions> emotionOptions
) : IEmotionService
{
    // How far from the baseline (on both axes) a decayed mood still counts as the baseline mood.
    public const double BaselineMoodRange = 0.1;

    private readonly EmotionOptions _options = emotionOptions.Value;

    public async Task<AgentEmotionState> GetAsync(int telegramUserId, CancellationToken cancellationToken = default)
    {
        var state = await dbContext.AgentEmotionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);

        return state is null
            ? new AgentEmotionState
            {
                TelegramUserId = telegramUserId,
                Valence = _options.BaselineValence,
                Arousal = _options.BaselineArousal,
                Mood = _options.BaselineMood,
                UpdatedAt = DateTime.UtcNow
            }
            : Decay(state, _options, DateTime.UtcNow);
    }

    /// <summary>
    /// Moves a stored mood back toward the baseline: half of the distance is gone after each
    /// HalfLifeHours. Computed on read, nothing is written. Once more than half has faded, the stored
    /// label and reason describe a moment that has passed, so the label is derived from the decayed
    /// numbers and the reason is dropped. HalfLifeHours &lt;= 0 turns decay off.
    /// </summary>
    public static AgentEmotionState Decay(AgentEmotionState stored, EmotionOptions options, DateTime nowUtc)
    {
        var hours = Math.Max(0, (nowUtc - stored.UpdatedAt).TotalHours);
        var factor = options.HalfLifeHours > 0 ? Math.Pow(0.5, hours / options.HalfLifeHours) : 1;

        var valence = options.BaselineValence + (stored.Valence - options.BaselineValence) * factor;
        var arousal = options.BaselineArousal + (stored.Arousal - options.BaselineArousal) * factor;
        var isStale = factor < 0.5;

        return new AgentEmotionState
        {
            TelegramUserId = stored.TelegramUserId,
            Valence = valence,
            Arousal = arousal,
            Mood = isStale ? DeriveMood(valence, arousal, options) : stored.Mood,
            Reason = isStale ? string.Empty : stored.Reason,
            UpdatedAt = stored.UpdatedAt,
            LastTurnId = stored.LastTurnId
        };
    }

    // Fixed quadrant labels around the baseline, for a mood whose own label has gone stale.
    private static string DeriveMood(double valence, double arousal, EmotionOptions options)
    {
        var valenceOffset = valence - options.BaselineValence;
        var arousalOffset = arousal - options.BaselineArousal;

        if (Math.Abs(valenceOffset) < BaselineMoodRange && Math.Abs(arousalOffset) < BaselineMoodRange)
        {
            return options.BaselineMood;
        }

        return (valenceOffset >= 0, arousalOffset >= 0) switch
        {
            (true, true) => "cheerful",
            (true, false) => "content",
            (false, true) => "tense",
            (false, false) => "a bit down"
        };
    }

    public async Task<bool> ApplyAsync(
        int telegramUserId,
        int turnId,
        EmotionDelta delta,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(telegramUserId, cancellationToken);
        if (current.LastTurnId >= turnId)
        {
            return false;
        }

        var maxDelta = _options.MaxDeltaPerTurn;
        var valence = Math.Clamp(current.Valence + Math.Clamp(delta.Valence, -maxDelta, maxDelta), -1, 1);
        var arousal = Math.Clamp(current.Arousal + Math.Clamp(delta.Arousal, -maxDelta, maxDelta), 0, 1);
        var mood = string.IsNullOrWhiteSpace(delta.Mood) ? current.Mood : delta.Mood.Trim();
        var reason = string.IsNullOrWhiteSpace(delta.Reason) ? current.Reason : delta.Reason.Trim();
        mood = mood[..Math.Min(mood.Length, AgentEmotionState.MaxMoodLength)];
        reason = reason[..Math.Min(reason.Length, AgentEmotionState.MaxReasonLength)];

        // One upsert statement, like AgentSessionStore, so nothing is left tracked on the shared DbContext.
        // The WHERE clause repeats the LastTurnId check in the database: when two updates for the same
        // user race, the one for the older turn writes nothing.
        var affectedRows = await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO agent_emotion_states (telegram_user_id, valence, arousal, mood, reason, updated_at, last_turn_id)
             VALUES ({telegramUserId}, {valence}, {arousal}, {mood}, {reason}, now(), {turnId})
             ON CONFLICT (telegram_user_id) DO UPDATE SET
                 valence = EXCLUDED.valence,
                 arousal = EXCLUDED.arousal,
                 mood = EXCLUDED.mood,
                 reason = EXCLUDED.reason,
                 updated_at = EXCLUDED.updated_at,
                 last_turn_id = EXCLUDED.last_turn_id
             WHERE agent_emotion_states.last_turn_id IS NULL OR agent_emotion_states.last_turn_id < EXCLUDED.last_turn_id
             """,
            cancellationToken);

        return affectedRows > 0;
    }
}
