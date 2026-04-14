namespace Assistant.Api.Features.UserManagement.Models;

public class UserMemoryConsolidationState
{
    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public int LastConsolidatedChatTurnId { get; set; }
    public bool IsJobQueued { get; set; }
    public DateTime? JobQueuedAtUtc { get; set; }
    public DateTime? JobStartedAtUtc { get; set; }
    public DateTime? LastCompletedAtUtc { get; set; }
    public DateTime? LastAttemptedAtUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
}
