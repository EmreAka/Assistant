namespace Assistant.Api.Features.Chat.Services;

public interface IDeferredIntentScheduler
{
    string ScheduleOneTime(Guid intentId, DateTime runAtUtc);
    string ScheduleRecurring(Guid intentId, string cronExpression, TimeZoneInfo timeZoneInfo);
    bool DeleteOneTime(string jobId);
    void DeleteRecurring(string recurringJobId);
}
