using Assistant.Api.Features.UserManagement.Models;

namespace Assistant.Api.Features.Chat.Models;

// The assistant's current mood toward one user (see EMOTION_PLAN.md). Valence runs from -1 (unhappy)
// to 1 (happy), arousal from 0 (calm) to 1 (energetic).
public class AgentEmotionState
{
    public const int MaxMoodLength = 40;
    public const int MaxReasonLength = 200;

    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public double Valence { get; set; }
    public double Arousal { get; set; }
    public string Mood { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }

    // The last chat turn applied; a turn with an id at or below it is skipped.
    public int? LastTurnId { get; set; }
}
