using Assistant.Api.Features.Chat.Models;

namespace Assistant.Api.Features.Chat.Services;

public interface IEmotionService
{
    /// <summary>Returns the user's mood, or the baseline mood when none is stored. The result is not tracked.</summary>
    Task<AgentEmotionState> GetAsync(int telegramUserId, CancellationToken cancellationToken = default);

    /// <summary>Same as GetAsync, for the user of a chat. Returns null when the chat has no registered user.</summary>
    Task<AgentEmotionState?> GetByChatIdAsync(long chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one chat turn's mood change. Returns false when the turn was already applied
    /// (turnId at or below the stored LastTurnId).
    /// </summary>
    Task<bool> ApplyAsync(int telegramUserId, int turnId, EmotionDelta delta, CancellationToken cancellationToken = default);
}

/// <summary>A mood change. An empty Mood or Reason keeps the current one.</summary>
public record EmotionDelta(double Valence, double Arousal, string Mood, string Reason);

/// <summary>A mood's position relative to the baseline; see EmotionService.GetQuadrant.</summary>
public enum MoodQuadrant
{
    Baseline,
    Cheerful, // happier, more energetic
    Content,  // happier, calmer
    Tense,    // unhappier, more energetic
    Down      // unhappier, calmer
}
