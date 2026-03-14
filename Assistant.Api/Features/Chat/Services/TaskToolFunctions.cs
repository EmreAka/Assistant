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
    IOptions<AiOptions> aiOptions,
    ILogger<TaskToolFunctions> logger
)
{
    [Description("Schedules a future task or action for the assistant to perform autonomously. " +
                 "Use this when the user asks you to do something later, check something at a specific time, or remind them in a personalized way.")]
    public async Task<string> ScheduleTask(
        [Description("The original instruction or goal to perform later. Be descriptive.")] string instruction,
        [Description("The local date/time to perform the task in ISO 8601 format (e.g. 2026-03-11T19:00:00).")] string runAtLocalIso)
    {
        try
        {
            var timeZoneId = aiOptions.Value.DefaultTimeZoneId;
            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

            if (!DateTime.TryParse(runAtLocalIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDateTime))
            {
                return "Error: Invalid date/time format. Use ISO 8601.";
            }

            var runAtUtc = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZoneInfo);
            if (runAtUtc <= DateTime.UtcNow)
            {
                return "Error: Cannot schedule a task in the past.";
            }

            var intent = new DeferredIntent
            {
                IntentId = Guid.NewGuid(),
                ChatId = chatId,
                OriginalInstruction = instruction,
                ScheduledAtUtc = runAtUtc,
                TimeZoneId = timeZoneId,
                Status = DeferredIntentStatuses.Pending
            };

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

            return $"Task scheduled successfully for {runAtLocalIso} ({timeZoneId}). ID: {intent.IntentId}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule task for ChatId: {ChatId}", chatId);
            return $"Error: {ex.Message}";
        }
    }
}
