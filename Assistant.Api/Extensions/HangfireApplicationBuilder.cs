using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Concretes;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Extensions;

public static class HangfireApplicationBuilder
{
    public static void UseHangfireRecurringJobs(this WebApplication app)
    {
        var jobManager = app.Services.GetRequiredService<IRecurringJobManager>();
        var aiOptions = app.Services.GetRequiredService<IOptions<AiOptions>>().Value;
        var timeZone = ResolveTimeZone(aiOptions.DefaultTimeZoneId);

        jobManager.AddOrUpdate<WorkdayEndReminderJob>(
            "workday-end-reminder",
            job => job.ExecuteAsync(),
            "0 14 * * 1-5",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });

        /* jobManager.AddOrUpdate<MemoryMaintenanceJob>(
            "memory-maintenance",
            job => job.ExecuteAsync(),
            aiOptions.MemoryConsolidationCron,
            new RecurringJobOptions
            {
                TimeZone = timeZone
            }); */
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
