using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.ChatFeatures;

public class TaskToolFunctionsTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 4, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ScheduleTask_CreatesOneTimeTask_AndReturnsTaskId()
    {
        await using var dbContext = CreateDbContext(nameof(ScheduleTask_CreatesOneTimeTask_AndReturnsTaskId));
        var scheduler = new FakeDeferredIntentScheduler
        {
            NextOneTimeJobId = "job-001"
        };
        var tool = CreateTool(1001, dbContext, scheduler);
        var runAtLocalIso = GetFutureLocalIso(TimeSpan.FromDays(1));

        var result = await tool.ScheduleTask("Review the report", runAtLocalIso: runAtLocalIso);

        var storedTask = await dbContext.DeferredIntents.SingleAsync();
        Assert.Contains("Task ID:", result);
        Assert.Equal(DeferredIntentStatuses.Scheduled, storedTask.Status);
        Assert.Equal("job-001", storedTask.HangfireJobId);
        Assert.NotNull(storedTask.ScheduledAtUtc);
        Assert.Single(scheduler.ScheduledOneTimeTasks);
        Assert.Equal(storedTask.IntentId, scheduler.ScheduledOneTimeTasks[0].IntentId);
    }

    [Fact]
    public async Task ScheduleTask_ReturnsPastError_WhenRequestedTimeIsNotInFuture()
    {
        await using var dbContext = CreateDbContext(nameof(ScheduleTask_ReturnsPastError_WhenRequestedTimeIsNotInFuture));
        var tool = CreateTool(1001, dbContext, new FakeDeferredIntentScheduler());
        var runAtLocalIso = GetLocalIso(FixedUtcNow.AddHours(-1));

        var result = await tool.ScheduleTask("Too late", runAtLocalIso: runAtLocalIso);

        Assert.Equal("Error: Cannot schedule a task in the past.", result);
        Assert.Empty(dbContext.DeferredIntents);
    }

    [Fact]
    public async Task ListTasks_ActiveFilter_IncludesScheduledAndRecurring_ButNotCancelled()
    {
        await using var dbContext = CreateDbContext(nameof(ListTasks_ActiveFilter_IncludesScheduledAndRecurring_ButNotCancelled));
        dbContext.DeferredIntents.AddRange(
            CreateIntent(1001, DeferredIntentStatuses.Scheduled, "Tomorrow reminder", scheduledAtUtc: FixedUtcNow.UtcDateTime.AddHours(4)),
            CreateIntent(1001, DeferredIntentStatuses.Recurring, "Daily standup", cronExpression: "0 9 * * *"),
            CreateIntent(1001, DeferredIntentStatuses.Cancelled, "Old cancelled reminder", scheduledAtUtc: FixedUtcNow.UtcDateTime.AddHours(6)),
            CreateIntent(2002, DeferredIntentStatuses.Scheduled, "Other chat task", scheduledAtUtc: FixedUtcNow.UtcDateTime.AddHours(2)));
        await dbContext.SaveChangesAsync();

        var result = await CreateTool(1001, dbContext, new FakeDeferredIntentScheduler()).ListTasks();

        Assert.Contains("Tomorrow reminder", result);
        Assert.Contains("Daily standup", result);
        Assert.DoesNotContain("Old cancelled reminder", result);
        Assert.DoesNotContain("Other chat task", result);
    }

    [Fact]
    public async Task CancelTask_CancelsRecurringTask_AndUnschedulesRecurringJob()
    {
        await using var dbContext = CreateDbContext(nameof(CancelTask_CancelsRecurringTask_AndUnschedulesRecurringJob));
        var scheduler = new FakeDeferredIntentScheduler();
        var intent = CreateIntent(
            1001,
            DeferredIntentStatuses.Recurring,
            "Daily expense check",
            cronExpression: "0 8 * * *",
            hangfireJobId: "deferred-intent-abc");
        dbContext.DeferredIntents.Add(intent);
        await dbContext.SaveChangesAsync();

        var result = await CreateTool(1001, dbContext, scheduler).CancelTask(intent.IntentId.ToString());

        var storedTask = await dbContext.DeferredIntents.SingleAsync();
        Assert.Equal($"Task cancelled successfully. Task ID: {intent.IntentId}", result);
        Assert.Equal(DeferredIntentStatuses.Cancelled, storedTask.Status);
        Assert.Equal("Cancelled by user request.", storedTask.ExecutionResult);
        Assert.Contains("deferred-intent-abc", scheduler.DeletedRecurringJobIds);
    }

    [Fact]
    public async Task RescheduleTask_ChangesScheduledTaskToRecurring()
    {
        await using var dbContext = CreateDbContext(nameof(RescheduleTask_ChangesScheduledTaskToRecurring));
        var scheduler = new FakeDeferredIntentScheduler
        {
            DeleteOneTimeResult = true,
            NextRecurringJobId = "deferred-intent-new"
        };
        var intent = CreateIntent(
            1001,
            DeferredIntentStatuses.Scheduled,
            "Check portfolio",
            scheduledAtUtc: FixedUtcNow.UtcDateTime.AddHours(5),
            hangfireJobId: "job-old");
        dbContext.DeferredIntents.Add(intent);
        await dbContext.SaveChangesAsync();

        var result = await CreateTool(1001, dbContext, scheduler).RescheduleTask(
            intent.IntentId.ToString(),
            cronExpression: "0 9 * * 1-5");

        var storedTask = await dbContext.DeferredIntents.SingleAsync();
        Assert.Equal($"Task rescheduled successfully with Cron '0 9 * * 1-5' (Europe/Istanbul). Task ID: {intent.IntentId}", result);
        Assert.Equal(DeferredIntentStatuses.Recurring, storedTask.Status);
        Assert.Equal("0 9 * * 1-5", storedTask.CronExpression);
        Assert.Null(storedTask.ScheduledAtUtc);
        Assert.Equal("deferred-intent-new", storedTask.HangfireJobId);
        Assert.Contains("job-old", scheduler.DeletedOneTimeJobIds);
        Assert.Single(scheduler.ScheduledRecurringTasks);
        Assert.Equal(intent.IntentId, scheduler.ScheduledRecurringTasks[0].IntentId);
        Assert.Equal("Europe/Istanbul", scheduler.ScheduledRecurringTasks[0].TimeZoneId);
    }

    private static TaskToolFunctions CreateTool(long chatId, ApplicationDbContext dbContext, FakeDeferredIntentScheduler scheduler)
    {
        return new TaskToolFunctions(
            chatId,
            dbContext,
            scheduler,
            CreateTimeService(),
            NullLogger<TaskToolFunctions>.Instance);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new TestApplicationDbContext(options);
    }

    private static IAssistantTimeService CreateTimeService()
    {
        return new AssistantTimeService(
            Options.Create(new AiProvidersOptions
            {
                DefaultTimeZoneId = "Europe/Istanbul"
            }),
            new FixedTimeProvider(FixedUtcNow));
    }

    private static string GetFutureLocalIso(TimeSpan offset)
    {
        return GetLocalIso(FixedUtcNow.Add(offset));
    }

    private static string GetLocalIso(DateTimeOffset utcTime)
    {
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var futureLocal = TimeZoneInfo.ConvertTimeFromUtc(utcTime.UtcDateTime, timeZoneInfo);
        return futureLocal.ToString("yyyy-MM-ddTHH:mm:ss");
    }

    private static DeferredIntent CreateIntent(
        long chatId,
        string status,
        string instruction,
        DateTime? scheduledAtUtc = null,
        string? cronExpression = null,
        string? hangfireJobId = null)
    {
        return new DeferredIntent
        {
            IntentId = Guid.NewGuid(),
            ChatId = chatId,
            OriginalInstruction = instruction,
            ScheduledAtUtc = scheduledAtUtc,
            CronExpression = cronExpression,
            TimeZoneId = "Europe/Istanbul",
            Status = status,
            HangfireJobId = hangfireJobId,
            CreatedAt = DateTime.UtcNow
        };
    }

    private sealed class FakeDeferredIntentScheduler : IDeferredIntentScheduler
    {
        public string NextOneTimeJobId { get; set; } = "job-default";
        public string NextRecurringJobId { get; set; } = "recurring-default";
        public bool DeleteOneTimeResult { get; set; } = true;
        public List<(Guid IntentId, DateTime RunAtUtc)> ScheduledOneTimeTasks { get; } = [];
        public List<(Guid IntentId, string CronExpression, string TimeZoneId)> ScheduledRecurringTasks { get; } = [];
        public List<string> DeletedOneTimeJobIds { get; } = [];
        public List<string> DeletedRecurringJobIds { get; } = [];

        public string ScheduleOneTime(Guid intentId, DateTime runAtUtc)
        {
            ScheduledOneTimeTasks.Add((intentId, runAtUtc));
            return NextOneTimeJobId;
        }

        public string ScheduleRecurring(Guid intentId, string cronExpression, TimeZoneInfo timeZoneInfo)
        {
            ScheduledRecurringTasks.Add((intentId, cronExpression, timeZoneInfo.Id));
            return NextRecurringJobId;
        }

        public bool DeleteOneTime(string jobId)
        {
            DeletedOneTimeJobIds.Add(jobId);
            return DeleteOneTimeResult;
        }

        public void DeleteRecurring(string recurringJobId)
        {
            DeletedRecurringJobIds.Add(recurringJobId);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Ignore<ChatTurn>();
        }
    }
}
