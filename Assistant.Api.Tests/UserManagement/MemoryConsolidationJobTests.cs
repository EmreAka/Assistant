using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryConsolidationJobTests
{
    [Fact]
    public async Task ExecuteAsync_SavesNewManifestVersion_AndAdvancesCheckpoint()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_SavesNewManifestVersion_AndAdvancesCheckpoint));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemoryManifests.Add(CreateManifest(1, "Existing memory", 1, isActive: true));
        var turns = await SeedTurnsAsync(dbContext, 1, 20);
        dbContext.UserMemoryConsolidationStates.Add(new UserMemoryConsolidationState
        {
            TelegramUserId = 1,
            IsJobQueued = true,
            JobQueuedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var fakeAgent = new FakeMemoryConsolidationAgentService("Existing memory\n\nUser prefers Aeropress.");
        var fakeCoordinator = new FakeMemoryConsolidationCoordinator();
        var job = CreateJob(dbContext, fakeAgent, fakeCoordinator, turnsThreshold: 20);

        await job.ExecuteAsync(1);

        var manifests = await dbContext.UserMemoryManifests
            .Where(x => x.TelegramUserId == 1)
            .OrderBy(x => x.Version)
            .ToListAsync();
        var state = await dbContext.UserMemoryConsolidationStates.SingleAsync(x => x.TelegramUserId == 1);

        Assert.Equal(2, manifests.Count);
        Assert.False(manifests[0].IsActive);
        Assert.True(manifests[1].IsActive);
        Assert.Equal("Existing memory\nUser prefers Aeropress.", manifests[1].Content);
        Assert.Equal(2, manifests[1].Version);
        Assert.Equal(turns.Max(x => x.Id), state.LastConsolidatedChatTurnId);
        Assert.False(state.IsJobQueued);
        Assert.NotNull(state.LastCompletedAtUtc);
        Assert.Equal([1], fakeCoordinator.QueuedUserIds);
        Assert.Equal(20, fakeAgent.LastRequest?.Turns.Count);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsNewManifestVersion_WhenManifestIsUnchanged()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_SkipsNewManifestVersion_WhenManifestIsUnchanged));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemoryManifests.Add(CreateManifest(1, "Existing memory", 1, isActive: true));
        var turns = await SeedTurnsAsync(dbContext, 1, 20);
        dbContext.UserMemoryConsolidationStates.Add(new UserMemoryConsolidationState
        {
            TelegramUserId = 1,
            IsJobQueued = true,
            JobQueuedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var fakeAgent = new FakeMemoryConsolidationAgentService("Existing memory");
        var fakeCoordinator = new FakeMemoryConsolidationCoordinator();
        var job = CreateJob(dbContext, fakeAgent, fakeCoordinator, turnsThreshold: 20);

        await job.ExecuteAsync(1);

        var manifests = await dbContext.UserMemoryManifests
            .Where(x => x.TelegramUserId == 1)
            .OrderBy(x => x.Version)
            .ToListAsync();
        var state = await dbContext.UserMemoryConsolidationStates.SingleAsync(x => x.TelegramUserId == 1);

        Assert.Single(manifests);
        Assert.True(manifests[0].IsActive);
        Assert.Equal(turns.Max(x => x.Id), state.LastConsolidatedChatTurnId);
        Assert.False(state.IsJobQueued);
        Assert.Equal([1], fakeCoordinator.QueuedUserIds);
    }

    private static MemoryConsolidationJob CreateJob(
        ApplicationDbContext dbContext,
        IMemoryConsolidationAgentService agentService,
        IMemoryConsolidationCoordinator coordinator,
        int turnsThreshold)
    {
        return new MemoryConsolidationJob(
            dbContext,
            new MemoryService(dbContext),
            agentService,
            coordinator,
            Options.Create(new MemoryConsolidationOptions
            {
                TurnsThreshold = turnsThreshold,
                StaleJobAfterMinutes = 15
            }),
            NullLogger<MemoryConsolidationJob>.Instance);
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

    private static async Task<List<ChatTurn>> SeedTurnsAsync(ApplicationDbContext dbContext, int telegramUserId, int count)
    {
        var createdAtUtc = DateTime.UtcNow.AddMinutes(-count);
        var turns = new List<ChatTurn>();

        for (var i = 0; i < count; i++)
        {
            turns.Add(new ChatTurn
            {
                TelegramUserId = telegramUserId,
                UserMessage = $"User message {i + 1}",
                AssistantMessage = $"Assistant message {i + 1}",
                CreatedAt = createdAtUtc.AddMinutes(i)
            });
        }

        dbContext.ChatTurns.AddRange(turns);
        await dbContext.SaveChangesAsync();
        return turns;
    }

    private static UserMemoryManifest CreateManifest(int telegramUserId, string content, int version, bool isActive)
    {
        return new UserMemoryManifest
        {
            TelegramUserId = telegramUserId,
            Content = content,
            Version = version,
            IsActive = isActive,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private sealed class FakeMemoryConsolidationAgentService(string manifest) : IMemoryConsolidationAgentService
    {
        public MemoryConsolidationRequest? LastRequest { get; private set; }

        public Task<string> ConsolidateAsync(MemoryConsolidationRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(manifest);
        }
    }

    private sealed class FakeMemoryConsolidationCoordinator : IMemoryConsolidationCoordinator
    {
        public List<int> QueuedUserIds { get; } = [];

        public Task QueueIfNeededAsync(int telegramUserId, CancellationToken cancellationToken)
        {
            QueuedUserIds.Add(telegramUserId);
            return Task.CompletedTask;
        }
    }
}
