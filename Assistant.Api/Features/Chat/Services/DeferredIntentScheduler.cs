using Hangfire;

namespace Assistant.Api.Features.Chat.Services;

public class DeferredIntentScheduler(
    IBackgroundJobClient backgroundJobClient,
    IRecurringJobManager recurringJobManager
) : IDeferredIntentScheduler
{
    public string ScheduleOneTime(Guid intentId, DateTime runAtUtc)
    {
        var enqueueAtUtc = ToUtc(runAtUtc);

        return backgroundJobClient.Schedule<DeferredIntentDispatchJob>(
            job => job.ExecuteAsync(intentId),
            new DateTimeOffset(enqueueAtUtc));
    }

    public string ScheduleRecurring(Guid intentId, string cronExpression, TimeZoneInfo timeZoneInfo)
    {
        var jobId = $"deferred-intent-{intentId}";

        recurringJobManager.AddOrUpdate<DeferredIntentDispatchJob>(
            jobId,
            job => job.ExecuteAsync(intentId),
            cronExpression,
            new RecurringJobOptions
            {
                TimeZone = timeZoneInfo
            });

        return jobId;
    }

    public bool DeleteOneTime(string jobId)
    {
        return backgroundJobClient.Delete(jobId);
    }

    public void DeleteRecurring(string recurringJobId)
    {
        recurringJobManager.RemoveIfExists(recurringJobId);
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
