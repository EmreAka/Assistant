using System.ComponentModel;
using System.Globalization;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class TaskToolFunctions(
    long chatId,
    ApplicationDbContext dbContext,
    IBackgroundJobClient backgroundJobClient,
    IRecurringJobManager recurringJobManager,
    IOptions<AiProvidersOptions> aiOptions,
    ILogger<TaskToolFunctions> logger
)
{
    [Description("Schedules a future task or action for the assistant to perform autonomously. " +
                 "Use this when the user asks you to do something later, check something at a specific time, " +
                 "or perform a recurring action (e.g., 'every morning', 'every Monday').")]
    public async Task<string> ScheduleTask(
        [Description("The original instruction or goal to perform later. Be descriptive.")] string instruction,
        [Description("For one-time tasks: The local date/time to perform the task in ISO 8601 format (e.g. 2026-03-11T19:00:00). Leave null for recurring tasks.")] string? runAtLocalIso = null,
        [Description("For recurring tasks: A standard Cron expression (e.g., '0 9 * * *' for every morning at 9 AM). Leave null for one-time tasks.")] string? cronExpression = null)
    {
        try
        {
            var timeZoneId = aiOptions.Value.DefaultTimeZoneId;
            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

            if (string.IsNullOrEmpty(runAtLocalIso) && string.IsNullOrEmpty(cronExpression))
            {
                return "Error: Either runAtLocalIso (for one-time) or cronExpression (for recurring) must be provided.";
            }

            var intent = new DeferredIntent
            {
                IntentId = Guid.NewGuid(),
                ChatId = chatId,
                OriginalInstruction = instruction,
                TimeZoneId = timeZoneId,
                CronExpression = cronExpression,
                Status = DeferredIntentStatuses.Pending
            };

            if (!string.IsNullOrEmpty(runAtLocalIso))
            {
                if (!DateTime.TryParse(runAtLocalIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDateTime))
                {
                    return "Error: Invalid date/time format. Use ISO 8601.";
                }

                var runAtUtc = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZoneInfo);
                if (runAtUtc <= DateTime.UtcNow)
                {
                    return "Error: Cannot schedule a task in the past.";
                }

                intent.ScheduledAtUtc = runAtUtc;
                dbContext.DeferredIntents.Add(intent);
                await dbContext.SaveChangesAsync();

                var delay = runAtUtc - DateTime.UtcNow;
                var jobId = backgroundJobClient.Schedule<DeferredIntentDispatchJob>(
                    job => job.ExecuteAsync(intent.IntentId),
                    delay
                );

                intent.HangfireJobId = jobId;
                intent.Status = DeferredIntentStatuses.Scheduled;
                await dbContext.SaveChangesAsync();

                return $"One-time task scheduled successfully for {runAtLocalIso} ({timeZoneId}). ID: {intent.IntentId}";
            }
            else
            {
                // Recurring task
                dbContext.DeferredIntents.Add(intent);
                await dbContext.SaveChangesAsync();

                var jobId = $"deferred-intent-{intent.IntentId}";
                recurringJobManager.AddOrUpdate<DeferredIntentDispatchJob>(
                    jobId,
                    job => job.ExecuteAsync(intent.IntentId),
                    cronExpression,
                    new RecurringJobOptions
                    {
                        TimeZone = timeZoneInfo
                    }
                );

                intent.HangfireJobId = jobId;
                intent.Status = DeferredIntentStatuses.Recurring;
                await dbContext.SaveChangesAsync();

                return $"Recurring task scheduled successfully with Cron '{cronExpression}' ({timeZoneId}). ID: {intent.IntentId}";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule task for ChatId: {ChatId}", chatId);
            return $"Error: {ex.Message}";
        }
    }
}
