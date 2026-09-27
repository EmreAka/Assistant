using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryExtractionJobTests
{
    [Fact]
    public async Task ExecuteAsync_AddsCandidatesWithoutNeighbors_WithoutReconcileCall()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_AddsCandidatesWithoutNeighbors_WithoutReconcileCall));
        SeedUser(dbContext, 1, 1001);
        var turns = await SeedTurnsAsync(dbContext, 1, 3);
        var agent = new FakeAgent
        {
            TurnFacts = [Fact("User likes espresso.", UserMemoryItemCategories.Preference, sourceTurnIds: [turns[0].Id])]
        };
        var (job, _, _, _) = CreateJob(dbContext, agent);

        await job.ExecuteAsync(1);

        var item = await dbContext.UserMemoryItems.SingleAsync();
        Assert.Equal("User likes espresso.", item.Text);
        Assert.Equal(UserMemoryItemCategories.Preference, item.Category);
        Assert.Equal(UserMemoryItemStatuses.Active, item.Status);
        Assert.Equal([turns[0].Id], item.SourceTurnIds);
        Assert.Empty(agent.ReconcileCalls);
        Assert.All(turns, turn => Assert.NotNull(turn.MemoryProcessedAt));
    }

    [Fact]
    public async Task ExecuteAsync_Update_SupersedesOldItemAndLinksReplacement()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_Update_SupersedesOldItemAndLinksReplacement));
        SeedUser(dbContext, 1, 1001);
        var existing = await SeedItemAsync(dbContext, 1, "User lives in Istanbul.", sourceTurnIds: [100]);
        var turns = await SeedTurnsAsync(dbContext, 1, 1);
        var agent = new FakeAgent
        {
            TurnFacts = [Fact("User lives in Ankara.", UserMemoryItemCategories.Identity, sourceTurnIds: [turns[0].Id])],
            Reconcile = candidates =>
            [
                new MemoryDecision(candidates[0].Index, "update", existing.Id, "User lives in Ankara.", UserMemoryItemCategories.Identity, false, "moved")
            ]
        };
        var (job, itemService, embeddings, _) = CreateJob(dbContext, agent);
        itemService.Neighbors["User lives in Ankara."] = [ToSearchResult(existing)];

        await job.ExecuteAsync(1);

        var replacement = await dbContext.UserMemoryItems.SingleAsync(x => x.Status == UserMemoryItemStatuses.Active);
        Assert.Equal(UserMemoryItemStatuses.Superseded, existing.Status);
        Assert.Equal(replacement.Id, existing.SupersededById);
        Assert.Equal("User lives in Ankara.", replacement.Text);
        Assert.Equal("moved", replacement.ChangeReason);
        Assert.Equal([100, turns[0].Id], replacement.SourceTurnIds);
        // The model kept the candidate's text, so its vector is reused instead of embedding again.
        Assert.Single(embeddings.EmbeddedTexts);
    }

    [Fact]
    public async Task ExecuteAsync_Delete_SoftDeletesTarget()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_Delete_SoftDeletesTarget));
        SeedUser(dbContext, 1, 1001);
        var existing = await SeedItemAsync(dbContext, 1, "User has a cat.");
        await SeedTurnsAsync(dbContext, 1, 1);
        var agent = new FakeAgent
        {
            TurnFacts = [Fact("User no longer has a cat.", UserMemoryItemCategories.Other)],
            Reconcile = candidates =>
            [
                new MemoryDecision(candidates[0].Index, "delete", existing.Id, string.Empty, string.Empty, false, "cat is gone")
            ]
        };
        var (job, itemService, _, _) = CreateJob(dbContext, agent);
        itemService.Neighbors["User no longer has a cat."] = [ToSearchResult(existing)];

        await job.ExecuteAsync(1);

        Assert.Equal(UserMemoryItemStatuses.Deleted, existing.Status);
        Assert.Equal("cat is gone", existing.ChangeReason);
        Assert.Equal(1, await dbContext.UserMemoryItems.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_Noop_OnlyBumpsLastConfirmedAt()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_Noop_OnlyBumpsLastConfirmedAt));
        SeedUser(dbContext, 1, 1001);
        var existing = await SeedItemAsync(dbContext, 1, "User likes espresso.");
        var previousConfirmedAt = existing.LastConfirmedAt;
        await SeedTurnsAsync(dbContext, 1, 1);
        var agent = new FakeAgent
        {
            TurnFacts = [Fact("User enjoys espresso.", UserMemoryItemCategories.Preference)],
            Reconcile = candidates =>
            [
                new MemoryDecision(candidates[0].Index, "noop", existing.Id, string.Empty, string.Empty, false, "known")
            ]
        };
        var (job, itemService, _, _) = CreateJob(dbContext, agent);
        itemService.Neighbors["User enjoys espresso."] = [ToSearchResult(existing)];

        await job.ExecuteAsync(1);

        Assert.Equal(1, await dbContext.UserMemoryItems.CountAsync());
        Assert.Equal(UserMemoryItemStatuses.Active, existing.Status);
        Assert.True(existing.LastConfirmedAt > previousConfirmedAt);
    }

    [Fact]
    public async Task ExecuteAsync_DropsDecisionWithTargetThatWasNotOffered()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_DropsDecisionWithTargetThatWasNotOffered));
        SeedUser(dbContext, 1, 1001);
        var offered = await SeedItemAsync(dbContext, 1, "User likes espresso.");
        var notOffered = await SeedItemAsync(dbContext, 1, "User has a cat.");
        var turns = await SeedTurnsAsync(dbContext, 1, 1);
        var agent = new FakeAgent
        {
            TurnFacts = [Fact("User likes flat white.", UserMemoryItemCategories.Preference)],
            Reconcile = candidates =>
            [
                new MemoryDecision(candidates[0].Index, "delete", notOffered.Id, string.Empty, string.Empty, false, "made up")
            ]
        };
        var (job, itemService, _, _) = CreateJob(dbContext, agent);
        itemService.Neighbors["User likes flat white."] = [ToSearchResult(offered)];

        await job.ExecuteAsync(1);

        Assert.Equal(UserMemoryItemStatuses.Active, notOffered.Status);
        Assert.Equal(UserMemoryItemStatuses.Active, offered.Status);
        Assert.Equal(2, await dbContext.UserMemoryItems.CountAsync());
        Assert.NotNull(turns[0].MemoryProcessedAt);
    }

    [Fact]
    public async Task ExecuteAsync_StoresCoreCandidateAsNonCore_WhenCoreLimitIsReached()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_StoresCoreCandidateAsNonCore_WhenCoreLimitIsReached));
        SeedUser(dbContext, 1, 1001);
        await SeedItemAsync(dbContext, 1, "User's name is Emre.", isCore: true);
        await SeedTurnsAsync(dbContext, 1, 1);
        var agent = new FakeAgent
        {
            TurnFacts = [Fact("User prefers short answers.", UserMemoryItemCategories.Preference, isCore: true)]
        };
        var (job, _, _, _) = CreateJob(dbContext, agent, maxCoreItems: 1);

        await job.ExecuteAsync(1);

        var added = await dbContext.UserMemoryItems.SingleAsync(x => x.Text == "User prefers short answers.");
        Assert.False(added.IsCore);
    }

    [Fact]
    public async Task ExecuteAsync_ValidatesExtractedFacts()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ValidatesExtractedFacts));
        SeedUser(dbContext, 1, 1001);
        var turns = await SeedTurnsAsync(dbContext, 1, 1);
        var agent = new FakeAgent
        {
            TurnFacts =
            [
                Fact("  User plays chess.  ", "board_games", sourceTurnIds: [turns[0].Id, 999]),
                Fact(new string('x', 301), UserMemoryItemCategories.Other),
                Fact("   ", UserMemoryItemCategories.Other)
            ]
        };
        var (job, _, _, _) = CreateJob(dbContext, agent, maxItemLength: 300);

        await job.ExecuteAsync(1);

        var item = await dbContext.UserMemoryItems.SingleAsync();
        Assert.Equal("User plays chess.", item.Text);
        Assert.Equal(UserMemoryItemCategories.Other, item.Category);
        Assert.Equal([turns[0].Id], item.SourceTurnIds);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsDuplicateCandidatesWithinOneRun()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_SkipsDuplicateCandidatesWithinOneRun));
        SeedUser(dbContext, 1, 1001);
        await SeedTurnsAsync(dbContext, 1, 2);
        var agent = new FakeAgent
        {
            TurnFacts =
            [
                Fact("User likes espresso.", UserMemoryItemCategories.Preference),
                Fact("User likes espresso.", UserMemoryItemCategories.Preference)
            ]
        };
        var (job, _, _, _) = CreateJob(dbContext, agent);

        await job.ExecuteAsync(1);

        Assert.Equal(1, await dbContext.UserMemoryItems.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_LeavesTurnsUnprocessed_WhenExtractionFails()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_LeavesTurnsUnprocessed_WhenExtractionFails));
        SeedUser(dbContext, 1, 1001);
        var turns = await SeedTurnsAsync(dbContext, 1, 2);
        var agent = new FakeAgent { ExtractException = new InvalidOperationException("model output was not valid JSON") };
        var (job, _, _, _) = CreateJob(dbContext, agent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ExecuteAsync(1));

        Assert.All(turns, turn => Assert.Null(turn.MemoryProcessedAt));
        Assert.Empty(dbContext.UserMemoryItems);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesAtMostMaxTurnsPerRun_AndRequeuesRemainingWork()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ProcessesAtMostMaxTurnsPerRun_AndRequeuesRemainingWork));
        SeedUser(dbContext, 1, 1001);
        var turns = await SeedTurnsAsync(dbContext, 1, 5);
        var agent = new FakeAgent();
        var (job, _, _, jobClient) = CreateJob(dbContext, agent, maxTurnsPerRun: 3, turnsThreshold: 2);

        await job.ExecuteAsync(1);

        Assert.Equal(turns.Take(3).Select(x => x.Id), Assert.Single(agent.ExtractCalls).Select(x => x.Id));
        Assert.All(turns.Take(3), turn => Assert.NotNull(turn.MemoryProcessedAt));
        Assert.All(turns.Skip(3), turn => Assert.Null(turn.MemoryProcessedAt));
        Assert.Equal(1, jobClient.CreatedJobCount);
    }

    [Fact]
    public async Task ExecuteAsync_ImportsManifestOnce_WhenUserHasNoItems()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ImportsManifestOnce_WhenUserHasNoItems));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemoryManifests.Add(new UserMemoryManifest
        {
            TelegramUserId = 1,
            Content = "Name: Emre. Likes espresso.",
            Version = 4,
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var agent = new FakeAgent
        {
            ManifestFacts =
            [
                Fact("User's name is Emre.", UserMemoryItemCategories.Identity, isCore: true),
                Fact("User likes espresso.", UserMemoryItemCategories.Preference)
            ]
        };
        var (job, _, _, _) = CreateJob(dbContext, agent);

        await job.ExecuteAsync(1);
        await job.ExecuteAsync(1);

        var items = await dbContext.UserMemoryItems.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(1, agent.ManifestCallCount);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsCore);
        Assert.All(items, item =>
        {
            Assert.Empty(item.SourceTurnIds);
            Assert.Equal("imported from manifest v4", item.ChangeReason);
        });
    }

    private static (MemoryExtractionJob Job, FakeMemoryItemService ItemService, FakeEmbeddingService Embeddings, FakeBackgroundJobClient JobClient) CreateJob(
        ApplicationDbContext dbContext,
        FakeAgent agent,
        int maxTurnsPerRun = 30,
        int turnsThreshold = 10,
        int maxCoreItems = 40,
        int maxItemLength = 300)
    {
        var embeddings = new FakeEmbeddingService();
        var itemService = new FakeMemoryItemService(dbContext, embeddings);
        var jobClient = new FakeBackgroundJobClient();

        var job = new MemoryExtractionJob(
            dbContext,
            new MemoryService(dbContext),
            itemService,
            agent,
            embeddings,
            jobClient,
            Options.Create(new MemoryItemOptions
            {
                TurnsThreshold = turnsThreshold,
                MaxTurnsPerRun = maxTurnsPerRun,
                MaxCandidatesPerRun = 20,
                MaxCoreItems = maxCoreItems,
                MaxItemLength = maxItemLength
            }),
            NullLogger<MemoryExtractionJob>.Instance);

        return (job, itemService, embeddings, jobClient);
    }

    private static CandidateFact Fact(string text, string category, bool isCore = false, int[]? sourceTurnIds = null)
    {
        return new CandidateFact(text, category, isCore, sourceTurnIds ?? []);
    }

    private static MemoryItemSearchResult ToSearchResult(UserMemoryItem item)
    {
        return new MemoryItemSearchResult(item.Id, item.Text, item.Category, item.IsCore, item.CreatedAt, 0.1);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            // Prefixed: InMemory database names are shared across all test classes.
            .UseInMemoryDatabase($"{nameof(MemoryExtractionJobTests)}.{databaseName}")
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

    private static async Task<UserMemoryItem> SeedItemAsync(
        ApplicationDbContext dbContext,
        int telegramUserId,
        string text,
        bool isCore = false,
        int[]? sourceTurnIds = null)
    {
        var createdAtUtc = DateTime.UtcNow.AddDays(-1);
        var item = new UserMemoryItem
        {
            TelegramUserId = telegramUserId,
            Text = text,
            Category = UserMemoryItemCategories.Other,
            IsCore = isCore,
            Status = UserMemoryItemStatuses.Active,
            SourceTurnIds = sourceTurnIds ?? [],
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc,
            LastConfirmedAt = createdAtUtc
        };

        dbContext.UserMemoryItems.Add(item);
        await dbContext.SaveChangesAsync();
        return item;
    }

    private sealed class FakeAgent : IMemoryExtractionAgentService
    {
        public IReadOnlyList<CandidateFact> TurnFacts { get; init; } = [];
        public IReadOnlyList<CandidateFact> ManifestFacts { get; init; } = [];
        public Func<IReadOnlyList<ReconcileCandidate>, IReadOnlyList<MemoryDecision>> Reconcile { get; init; } = _ => [];
        public Exception? ExtractException { get; init; }

        public List<IReadOnlyList<MemoryExtractionTurn>> ExtractCalls { get; } = [];
        public List<IReadOnlyList<ReconcileCandidate>> ReconcileCalls { get; } = [];
        public int ManifestCallCount { get; private set; }

        public Task<IReadOnlyList<CandidateFact>> ExtractFromTurnsAsync(IReadOnlyList<MemoryExtractionTurn> turns, CancellationToken cancellationToken)
        {
            if (ExtractException is not null)
            {
                throw ExtractException;
            }

            ExtractCalls.Add(turns);
            return Task.FromResult(TurnFacts);
        }

        public Task<IReadOnlyList<CandidateFact>> ExtractFromManifestAsync(string manifest, CancellationToken cancellationToken)
        {
            ManifestCallCount++;
            return Task.FromResult(ManifestFacts);
        }

        // Mirrors the real service: an empty list never reaches the model.
        public Task<IReadOnlyList<MemoryDecision>> ReconcileAsync(IReadOnlyList<ReconcileCandidate> candidates, CancellationToken cancellationToken)
        {
            if (candidates.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<MemoryDecision>>([]);
            }

            ReconcileCalls.Add(candidates);
            return Task.FromResult(Reconcile(candidates));
        }
    }

    // Each distinct text gets its own one-hot vector: equal texts are identical (distance 0),
    // different texts are unrelated (distance 1).
    private sealed class FakeEmbeddingService : IChatTurnEmbeddingService
    {
        private readonly Dictionary<string, Vector> _vectors = new(StringComparer.Ordinal);

        public List<string> EmbeddedTexts { get; } = [];

        public string? TextFor(Vector vector)
        {
            return _vectors.FirstOrDefault(x => ReferenceEquals(x.Value, vector)).Key;
        }

        public Task<Vector> EmbedDocumentAsync(string text, CancellationToken cancellationToken)
        {
            EmbeddedTexts.Add(text);
            if (!_vectors.TryGetValue(text, out var vector))
            {
                var values = new float[64];
                values[_vectors.Count] = 1;
                vector = new Vector(values);
                _vectors[text] = vector;
            }

            return Task.FromResult(vector);
        }

        public Task<Vector> EmbedQueryAsync(string text, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    // InMemory can't run CosineDistance, so neighbors are set up per candidate text.
    private sealed class FakeMemoryItemService(ApplicationDbContext dbContext, FakeEmbeddingService embeddings) : IMemoryItemService
    {
        public Dictionary<string, IReadOnlyList<MemoryItemSearchResult>> Neighbors { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<MemoryItemSearchResult>> FindNeighborsAsync(int telegramUserId, Vector vector, CancellationToken cancellationToken)
        {
            var text = embeddings.TextFor(vector);
            return Task.FromResult(text is not null && Neighbors.TryGetValue(text, out var neighbors) ? neighbors : []);
        }

        public Task<bool> HasAnyItemsAsync(int telegramUserId, CancellationToken cancellationToken)
        {
            return dbContext.UserMemoryItems.AnyAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);
        }

        public Task<IReadOnlyList<MemoryItemSummary>> GetCoreItemsAsync(long chatId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MemoryItemSummary>> GetActiveItemsAsync(long chatId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MemoryItemSearchResult>> SearchAsync(long chatId, Vector queryVector, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
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
