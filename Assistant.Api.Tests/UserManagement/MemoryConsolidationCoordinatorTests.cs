using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryConsolidationCoordinatorTests
{
    [Fact]
    public async Task QueueIfNeededAsync_DoesNotQueue_WhenPendingTurnsAreBelowThreshold()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_DoesNotQueue_WhenPendingTurnsAreBelowThreshold));
        SeedUser(dbContext, 1, 1001);
        await SeedTurnsAsync(dbContext, 1, 19);
        var scheduler = new FakeMemoryConsolidationScheduler();
        var coordinator = CreateCoordinator(dbContext, scheduler, turnsThreshold: 20);

        await coordinator.QueueIfNeededAsync(1, CancellationToken.None);

        var state = await dbContext.UserMemoryConsolidationStates.SingleAsync(x => x.TelegramUserId == 1);
        Assert.Empty(scheduler.EnqueuedUserIds);
        Assert.False(state.IsJobQueued);
    }

    [Fact]
    public async Task QueueIfNeededAsync_QueuesJob_WhenPendingTurnsReachThreshold()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_QueuesJob_WhenPendingTurnsReachThreshold));
        SeedUser(dbContext, 1, 1001);
        await SeedTurnsAsync(dbContext, 1, 20);
        var scheduler = new FakeMemoryConsolidationScheduler();
        var coordinator = CreateCoordinator(dbContext, scheduler, turnsThreshold: 20);

        await coordinator.QueueIfNeededAsync(1, CancellationToken.None);

        var state = await dbContext.UserMemoryConsolidationStates.SingleAsync(x => x.TelegramUserId == 1);
        Assert.Equal([1], scheduler.EnqueuedUserIds);
        Assert.True(state.IsJobQueued);
        Assert.NotNull(state.JobQueuedAtUtc);
    }

    [Fact]
    public async Task QueueIfNeededAsync_CountsOnlyRequestedUsersTurns()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_CountsOnlyRequestedUsersTurns));
        SeedUser(dbContext, 1, 1001);
        SeedUser(dbContext, 2, 2002);
        await SeedTurnsAsync(dbContext, 1, 19);
        await SeedTurnsAsync(dbContext, 2, 50);
        var scheduler = new FakeMemoryConsolidationScheduler();
        var coordinator = CreateCoordinator(dbContext, scheduler, turnsThreshold: 20);

        await coordinator.QueueIfNeededAsync(1, CancellationToken.None);

        Assert.Empty(scheduler.EnqueuedUserIds);
        var state = await dbContext.UserMemoryConsolidationStates.SingleAsync(x => x.TelegramUserId == 1);
        Assert.False(state.IsJobQueued);
    }

    private static MemoryConsolidationCoordinator CreateCoordinator(
        ApplicationDbContext dbContext,
        IMemoryConsolidationScheduler scheduler,
        int turnsThreshold)
    {
        return new MemoryConsolidationCoordinator(
            dbContext,
            scheduler,
            Options.Create(new MemoryConsolidationOptions
            {
                TurnsThreshold = turnsThreshold,
                StaleJobAfterMinutes = 15
            }),
            NullLogger<MemoryConsolidationCoordinator>.Instance);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static void SeedUser(ApplicationDbContext dbContext, int userId, long chatId)
    {
        dbContext.TelegramUsers.Add(new TelegramUser
        {
            Id = userId,
            ChatId = chatId,
            CreatedAt = DateTime.UtcNow,
            FirstName = $"User{userId}",
            UserName = $"user{userId}"
        });
        dbContext.SaveChanges();
    }

    private static async Task SeedTurnsAsync(ApplicationDbContext dbContext, int telegramUserId, int count)
    {
        var createdAtUtc = DateTime.UtcNow.AddMinutes(-count);
        for (var i = 0; i < count; i++)
        {
            dbContext.ChatTurns.Add(new ChatTurn
            {
                TelegramUserId = telegramUserId,
                UserMessage = $"User message {i + 1}",
                AssistantMessage = $"Assistant message {i + 1}",
                CreatedAt = createdAtUtc.AddMinutes(i)
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private sealed class FakeMemoryConsolidationScheduler : IMemoryConsolidationScheduler
    {
        public List<int> EnqueuedUserIds { get; } = [];

        public string Enqueue(int telegramUserId)
        {
            EnqueuedUserIds.Add(telegramUserId);
            return $"job-{telegramUserId}-{EnqueuedUserIds.Count}";
        }
    }
}
