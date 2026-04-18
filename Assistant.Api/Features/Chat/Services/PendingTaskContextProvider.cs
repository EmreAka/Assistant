using Assistant.Api.Data;
using Assistant.Api.Features.Chat.Models;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.Chat.Services;

public class PendingTaskContextProvider(
    long chatId,
    ApplicationDbContext dbContext,
    IAssistantTimeService assistantTimeService,
    int maxTasks = 8
) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (maxTasks <= 0)
        {
            return new AIContext();
        }

        var nowUtc = assistantTimeService.UtcNow;

        var pendingTasks = await dbContext.DeferredIntents
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Where(x =>
                x.Status == DeferredIntentStatuses.Pending
                || x.Status == DeferredIntentStatuses.Scheduled
                || x.Status == DeferredIntentStatuses.Recurring)
            .OrderBy(x => x.ScheduledAtUtc == null ? 1 : 0)
            .ThenBy(x => x.ScheduledAtUtc)
            .ThenByDescending(x => x.CreatedAt)
            .Take(maxTasks)
            .ToListAsync(cancellationToken);

        if (pendingTasks.Count == 0)
        {
            return new AIContext();
        }

        var taskLines = pendingTasks
            .Select(task =>
            {
                var timing = task.IsRecurring ? "recurring" : BuildTimingLabel(task.ScheduledAtUtc ?? DateTime.MinValue, nowUtc);
                var schedule = task.IsRecurring ? $"Cron: {task.CronExpression}" : FormatScheduledTime(task, assistantTimeService);
                return $"- Task ID: {task.IntentId} | [{timing}] {schedule}: {task.OriginalInstruction}";
            })
            .ToArray();

        return new AIContext
        {
            Instructions = $"""
                             Open loops and pending tasks:
                             {string.Join(Environment.NewLine, taskLines)}

                             Use this only when relevant.
                             These lines include Task IDs. Reuse the exact Task ID when cancelling or rescheduling.
                             Do not duplicate or reschedule an existing pending task unless the user explicitly asks to change it.
                             """
        };
    }

    private static string BuildTimingLabel(DateTime scheduledAtUtc, DateTime nowUtc)
    {
        return scheduledAtUtc <= nowUtc ? "overdue" : "scheduled";
    }

    private static string FormatScheduledTime(DeferredIntent task, IAssistantTimeService assistantTimeService)
    {
        if (!task.ScheduledAtUtc.HasValue) return "Unscheduled";

        var displayTime = assistantTimeService.FormatUtcForDisplay(task.ScheduledAtUtc.Value, task.TimeZoneId, "yyyy-MM-dd HH:mm");
        return displayTime.EndsWith(" UTC", StringComparison.Ordinal)
            ? displayTime
            : $"{displayTime} {task.TimeZoneId}";
    }
}
