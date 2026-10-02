using Assistant.Api.Features.Chat.Models;

namespace Assistant.Api.Features.Chat.Services;

public interface IEmotionService
{
    /// <summary>Returns the user's mood, or the baseline mood when none is stored. The result is not tracked.</summary>
    Task<AgentEmotionState> GetAsync(int telegramUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one chat turn's mood change. Returns false when the turn was already applied
    /// (turnId at or below the stored LastTurnId).
    /// </summary>
    Task<bool> ApplyAsync(int telegramUserId, int turnId, EmotionDelta delta, CancellationToken cancellationToken = default);
}

/// <summary>A mood change. An empty Mood or Reason keeps the current one.</summary>
public record EmotionDelta(double Valence, double Arousal, string Mood, string Reason);
