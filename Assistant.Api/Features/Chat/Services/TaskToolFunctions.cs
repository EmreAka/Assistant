using System.ComponentModel;
using System.Globalization;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class TaskToolFunctions(
    long chatId,
    ApplicationDbContext dbContext,
    IDeferredIntentScheduler deferredIntentScheduler,
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

            var hasRunAt = !string.IsNullOrWhiteSpace(runAtLocalIso);
            var hasCron = !string.IsNullOrWhiteSpace(cronExpression);

            if (hasRunAt == hasCron)
            {
                return "Error: Provide exactly one of runAtLocalIso (one-time) or cronExpression (recurring).";
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

            if (hasRunAt)
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

                var jobId = deferredIntentScheduler.ScheduleOneTime(intent.IntentId, runAtUtc);

                intent.HangfireJobId = jobId;
                intent.Status = DeferredIntentStatuses.Scheduled;
                await dbContext.SaveChangesAsync();

                return $"One-time task scheduled successfully for {runAtLocalIso} ({timeZoneId}). Task ID: {intent.IntentId}";
            }
            else
            {
                dbContext.DeferredIntents.Add(intent);
                await dbContext.SaveChangesAsync();

                var jobId = deferredIntentScheduler.ScheduleRecurring(intent.IntentId, cronExpression!, timeZoneInfo);

                intent.HangfireJobId = jobId;
                intent.Status = DeferredIntentStatuses.Recurring;
                await dbContext.SaveChangesAsync();

                return $"Recurring task scheduled successfully with Cron '{cronExpression}' ({timeZoneId}). Task ID: {intent.IntentId}";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule task for ChatId: {ChatId}", chatId);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the user's tasks and reminders. Use this before cancelling or rescheduling when you need a task ID or need to see what is currently active.")]
    public async Task<string> ListTasks(
        [Description("Task filter. Use active, scheduled, recurring, completed, cancelled, failed, or all.")] string statusFilter = "active",
        [Description("Maximum number of tasks to return.")] int limit = 10)
    {
        try
        {
            var normalizedLimit = Math.Clamp(limit, 1, 20);
            var tasksQuery = dbContext.DeferredIntents
                .AsNoTracking()
                .Where(x => x.ChatId == chatId);

            tasksQuery = ApplyStatusFilter(tasksQuery, statusFilter);

            var tasks = await tasksQuery
                .OrderBy(x => x.ScheduledAtUtc == null ? 1 : 0)
                .ThenBy(x => x.ScheduledAtUtc)
                .ThenByDescending(x => x.CreatedAt)
                .Take(normalizedLimit)
                .ToListAsync();

            if (tasks.Count == 0)
            {
                return $"No tasks found for filter '{NormalizeStatusFilter(statusFilter)}'.";
            }

            var lines = tasks
                .Select(FormatTaskLine)
                .ToArray();

            return $$"""
                     Tasks for current chat:
                     {{string.Join(Environment.NewLine, lines)}}
                     """;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list tasks for ChatId: {ChatId}", chatId);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Cancels an existing scheduled or recurring task using its Task ID. If you do not know the Task ID, call ListTasks first.")]
    public async Task<string> CancelTask(
        [Description("The Task ID to cancel. Use the exact GUID returned by ListTasks or ScheduleTask.")] string taskId)
    {
        try
        {
            if (!TryParseTaskId(taskId, out var intentId))
            {
                return "Error: Invalid taskId format. Use the exact Task ID.";
            }

            var intent = await GetTaskAsync(intentId);
            if (intent is null)
            {
                return "Error: Task not found for this chat.";
            }

            if (!IsActive(intent.Status))
            {
                return $"Error: Task is already {intent.Status}.";
            }

            if (!await UnscheduleIfNeededAsync(intent))
            {
                return "Error: Existing Hangfire job could not be cancelled, so the task was left unchanged.";
            }

            intent.Status = DeferredIntentStatuses.Cancelled;
            intent.ExecutionResult = "Cancelled by user request.";
            await dbContext.SaveChangesAsync();

            return $"Task cancelled successfully. Task ID: {intent.IntentId}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel task for ChatId: {ChatId}", chatId);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Reschedules an existing task using its Task ID. You can change a one-time task to another time, change a recurring cron schedule, or switch between one-time and recurring.")]
    public async Task<string> RescheduleTask(
        [Description("The Task ID to reschedule. Use the exact GUID returned by ListTasks or ScheduleTask.")] string taskId,
        [Description("The new local date/time for a one-time schedule in ISO 8601 format, such as 2026-03-11T19:00:00. Leave null when switching to a recurring cron schedule.")] string? runAtLocalIso = null,
        [Description("The new Cron expression for a recurring schedule. Leave null when switching to a one-time local datetime.")] string? cronExpression = null)
    {
        try
        {
            if (!TryParseTaskId(taskId, out var intentId))
            {
                return "Error: Invalid taskId format. Use the exact Task ID.";
            }

            var hasRunAt = !string.IsNullOrWhiteSpace(runAtLocalIso);
            var hasCron = !string.IsNullOrWhiteSpace(cronExpression);
            if (hasRunAt == hasCron)
            {
                return "Error: Provide exactly one of runAtLocalIso (one-time) or cronExpression (recurring).";
            }

            var intent = await GetTaskAsync(intentId);
            if (intent is null)
            {
                return "Error: Task not found for this chat.";
            }

            if (!IsActive(intent.Status))
            {
                return $"Error: Task is already {intent.Status} and cannot be rescheduled.";
            }

            if (!await UnscheduleIfNeededAsync(intent))
            {
                return "Error: Existing Hangfire job could not be replaced, so the task was left unchanged.";
            }

            var timeZoneId = aiOptions.Value.DefaultTimeZoneId;
            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            intent.TimeZoneId = timeZoneId;

            if (hasRunAt)
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
                intent.CronExpression = null;
                intent.HangfireJobId = deferredIntentScheduler.ScheduleOneTime(intent.IntentId, runAtUtc);
                intent.Status = DeferredIntentStatuses.Scheduled;
                intent.ExecutionResult = null;
                await dbContext.SaveChangesAsync();

                return $"Task rescheduled successfully for {runAtLocalIso} ({timeZoneId}). Task ID: {intent.IntentId}";
            }

            intent.ScheduledAtUtc = null;
            intent.CronExpression = cronExpression;
            intent.HangfireJobId = deferredIntentScheduler.ScheduleRecurring(intent.IntentId, cronExpression!, timeZoneInfo);
            intent.Status = DeferredIntentStatuses.Recurring;
            intent.ExecutionResult = null;
            await dbContext.SaveChangesAsync();

            return $"Task rescheduled successfully with Cron '{cronExpression}' ({timeZoneId}). Task ID: {intent.IntentId}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reschedule task for ChatId: {ChatId}", chatId);
            return $"Error: {ex.Message}";
        }
    }

    private static string NormalizeStatusFilter(string? statusFilter)
    {
        var normalized = statusFilter?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "active" : normalized;
    }

    private static IQueryable<DeferredIntent> ApplyStatusFilter(IQueryable<DeferredIntent> query, string? statusFilter)
    {
        return NormalizeStatusFilter(statusFilter) switch
        {
            "all" => query,
            "active" => query.Where(x =>
                x.Status == DeferredIntentStatuses.Pending
                || x.Status == DeferredIntentStatuses.Scheduled
                || x.Status == DeferredIntentStatuses.Recurring),
            "scheduled" => query.Where(x => x.Status == DeferredIntentStatuses.Scheduled),
            "recurring" => query.Where(x => x.Status == DeferredIntentStatuses.Recurring),
            "completed" => query.Where(x => x.Status == DeferredIntentStatuses.Completed),
            "cancelled" => query.Where(x => x.Status == DeferredIntentStatuses.Cancelled),
            "failed" => query.Where(x => x.Status == DeferredIntentStatuses.Failed),
            _ => query.Where(x =>
                x.Status == DeferredIntentStatuses.Pending
                || x.Status == DeferredIntentStatuses.Scheduled
                || x.Status == DeferredIntentStatuses.Recurring)
        };
    }

    private static bool IsActive(string status)
    {
        return status == DeferredIntentStatuses.Pending
            || status == DeferredIntentStatuses.Scheduled
            || status == DeferredIntentStatuses.Recurring;
    }

    private async Task<DeferredIntent?> GetTaskAsync(Guid intentId)
    {
        return await dbContext.DeferredIntents
            .FirstOrDefaultAsync(x => x.ChatId == chatId && x.IntentId == intentId);
    }

    private async Task<bool> UnscheduleIfNeededAsync(DeferredIntent intent)
    {
        if (intent.Status == DeferredIntentStatuses.Recurring)
        {
            if (!string.IsNullOrWhiteSpace(intent.HangfireJobId))
            {
                deferredIntentScheduler.DeleteRecurring(intent.HangfireJobId);
            }

            return true;
        }

        if ((intent.Status == DeferredIntentStatuses.Scheduled || intent.Status == DeferredIntentStatuses.Pending)
            && !string.IsNullOrWhiteSpace(intent.HangfireJobId))
        {
            return await Task.FromResult(deferredIntentScheduler.DeleteOneTime(intent.HangfireJobId));
        }

        return true;
    }

    private static bool TryParseTaskId(string? taskId, out Guid intentId)
    {
        return Guid.TryParse(taskId, out intentId);
    }

    private static string FormatTaskLine(DeferredIntent intent)
    {
        var schedule = intent.IsRecurring
            ? $"Cron: {intent.CronExpression}"
            : FormatScheduledTime(intent);

        return $"- Task ID: {intent.IntentId} | Status: {intent.Status} | Schedule: {schedule} | Instruction: {intent.OriginalInstruction}";
    }

    private static string FormatScheduledTime(DeferredIntent task)
    {
        if (!task.ScheduledAtUtc.HasValue)
        {
            return "Unscheduled";
        }

        try
        {
            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(task.ScheduledAtUtc.Value, timeZoneInfo);
            return $"{localTime:yyyy-MM-dd HH:mm:ss} {task.TimeZoneId}";
        }
        catch (TimeZoneNotFoundException)
        {
            return $"{task.ScheduledAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
        }
        catch (InvalidTimeZoneException)
        {
            return $"{task.ScheduledAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
        }
    }
}
