namespace Assistant.Api.Features.Chat.Models;

public class DeferredIntent
{
    public int Id { get; set; }
    public Guid IntentId { get; set; }
    public long ChatId { get; set; }
    public string OriginalInstruction { get; set; } = string.Empty;
    public DateTime? ScheduledAtUtc { get; set; }
    public string? CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public string Status { get; set; } = DeferredIntentStatuses.Pending;
    public string? HangfireJobId { get; set; }
    public string? ExecutionResult { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExecutedAtUtc { get; set; }

    public bool IsRecurring => !string.IsNullOrEmpty(CronExpression);
}

public static class DeferredIntentStatuses
{
    public const string Pending = "pending";
    public const string Scheduled = "scheduled";
    public const string Recurring = "recurring";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
