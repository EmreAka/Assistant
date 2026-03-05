namespace Assistant.Api.Domain.Entities;

public class Reminder
{
    public int Id { get; set; }
    public Guid ReminderId { get; set; }
    public long ChatId { get; set; }
    public long? TopicId { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsRecurring { get; set; }
    public string? CronExpression { get; set; }
    public DateTime? RunAtUtc { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public string HangfireJobId { get; set; } = string.Empty;
    public string Status { get; set; } = ReminderStatuses.PendingSchedule;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastSentAtUtc { get; set; }
}
