using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryExtractionCoordinatorTests
{
    [Fact]
    public async Task QueueIfNeededAsync_DoesNotQueue_WhenPendingTurnsAreBelowThreshold()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_DoesNotQueue_WhenPendingTurnsAreBelowThreshold));
        SeedUser(dbContext, 1, 1001);
        await SeedTurnsAsync(dbContext, 1, 9);
        var jobClient = new FakeBackgroundJobClient();

        await CreateCoordinator(dbContext, jobClient, turnsThreshold: 10).QueueIfNeededAsync(1, CancellationToken.None);

        Assert.Equal(0, jobClient.CreatedJobCount);
    }

    [Fact]
    public async Task QueueIfNeededAsync_QueuesJob_WhenPendingTurnsReachThreshold()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_QueuesJob_WhenPendingTurnsReachThreshold));
        SeedUser(dbContext, 1, 1001);
        await SeedTurnsAsync(dbContext, 1, 10);
        var jobClient = new FakeBackgroundJobClient();

        await CreateCoordinator(dbContext, jobClient, turnsThreshold: 10).QueueIfNeededAsync(1, CancellationToken.None);

        Assert.Equal(1, jobClient.CreatedJobCount);
    }

    [Fact]
    public async Task QueueIfNeededAsync_IgnoresProcessedTurns()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_IgnoresProcessedTurns));
        SeedUser(dbContext, 1, 1001);
        await SeedTurnsAsync(dbContext, 1, 10, memoryProcessedAt: DateTime.UtcNow);
        await SeedTurnsAsync(dbContext, 1, 9);
        var jobClient = new FakeBackgroundJobClient();

        await CreateCoordinator(dbContext, jobClient, turnsThreshold: 10).QueueIfNeededAsync(1, CancellationToken.None);

        Assert.Equal(0, jobClient.CreatedJobCount);
    }

    [Fact]
    public async Task QueueIfNeededAsync_QueuesImport_WhenUserHasManifestButNoItems()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_QueuesImport_WhenUserHasManifestButNoItems));
        SeedUser(dbContext, 1, 1001);
        SeedManifest(dbContext, 1);
        await SeedTurnsAsync(dbContext, 1, 1);
        var jobClient = new FakeBackgroundJobClient();

        await CreateCoordinator(dbContext, jobClient, turnsThreshold: 10).QueueIfNeededAsync(1, CancellationToken.None);

        Assert.Equal(1, jobClient.CreatedJobCount);
    }

    [Fact]
    public async Task QueueIfNeededAsync_DoesNotQueueImport_WhenUserAlreadyHasItems()
    {
        await using var dbContext = CreateDbContext(nameof(QueueIfNeededAsync_DoesNotQueueImport_WhenUserAlreadyHasItems));
        SeedUser(dbContext, 1, 1001);
        SeedManifest(dbContext, 1);
        dbContext.UserMemoryItems.Add(new UserMemoryItem
        {
            TelegramUserId = 1,
            Text = "User likes espresso.",
            Status = UserMemoryItemStatuses.Deleted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastConfirmedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        await SeedTurnsAsync(dbContext, 1, 1);
        var jobClient = new FakeBackgroundJobClient();

        await CreateCoordinator(dbContext, jobClient, turnsThreshold: 10).QueueIfNeededAsync(1, CancellationToken.None);

        Assert.Equal(0, jobClient.CreatedJobCount);
    }

    private static MemoryExtractionCoordinator CreateCoordinator(
        ApplicationDbContext dbContext,
        IBackgroundJobClient jobClient,
        int turnsThreshold)
    {
        var options = Options.Create(new MemoryItemOptions { TurnsThreshold = turnsThreshold });

        return new MemoryExtractionCoordinator(
            dbContext,
            new MemoryItemService(dbContext, options, NullLogger<MemoryItemService>.Instance),
            jobClient,
            options,
            NullLogger<MemoryExtractionCoordinator>.Instance);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            // Prefixed: InMemory database names are shared across all test classes.
            .UseInMemoryDatabase($"{nameof(MemoryExtractionCoordinatorTests)}.{databaseName}")
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

    private static void SeedManifest(ApplicationDbContext dbContext, int telegramUserId)
    {
        dbContext.UserMemoryManifests.Add(new UserMemoryManifest
        {
            TelegramUserId = telegramUserId,
            Content = "User likes espresso.",
            Version = 1,
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        });
        dbContext.SaveChanges();
    }

    private static async Task SeedTurnsAsync(
        ApplicationDbContext dbContext,
        int telegramUserId,
        int count,
        DateTime? memoryProcessedAt = null)
    {
        for (var i = 0; i < count; i++)
        {
            dbContext.ChatTurns.Add(new ChatTurn
            {
                TelegramUserId = telegramUserId,
                UserMessage = $"User message {i + 1}",
                AssistantMessage = $"Assistant message {i + 1}",
                CreatedAt = DateTime.UtcNow,
                MemoryProcessedAt = memoryProcessedAt
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private sealed class FakeBackgroundJobClient : IBackgroundJobClient
    {
        public int CreatedJobCount { get; private set; }

        public string Create(Job job, IState state)
        {
            CreatedJobCount++;
            return $"job-{CreatedJobCount}";
        }

        public bool ChangeState(string jobId, IState state, string expectedState)
        {
            throw new NotSupportedException();
        }
    }
}
