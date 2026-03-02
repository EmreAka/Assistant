using Assistant.Api.Services.Concretes;
using Hangfire;

namespace Assistant.Api.Extensions;

public static class HangfireApplicationBuilder
{
    public static void UseHangfireRecurringJobs(this WebApplication app)
    {
        var jobManager = app.Services.GetRequiredService<IRecurringJobManager>();

        jobManager.AddOrUpdate<WorkdayEndReminderJob>(
            "workday-end-reminder",
            job => job.ExecuteAsync(),
            "0 14 * * 1-5",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });
    }
}
