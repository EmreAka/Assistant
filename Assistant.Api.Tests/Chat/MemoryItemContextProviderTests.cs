using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Assistant.Api.Tests.ChatFeatures;

public class MemoryItemContextProviderTests
{
    private const long ChatId = 1001;
    private static readonly Vector QueryVector = new(new float[] { 1, 0, 0 });

    [Fact]
    public async Task ProvideAIContextAsync_IncludesCoreAndRelevantItems()
    {
        var service = new FakeMemoryItemService
        {
            CoreItems = [new MemoryItemSummary(1, "User's name is Emre.", "identity", true, DateTime.UtcNow)],
            RelevantItems = [new MemoryItemSearchResult(7, "User likes espresso.", "preference", false, DateTime.UtcNow, 0.2)]
        };
        var provider = new TestMemoryItemContextProvider(service, QueryVector);

        var context = await provider.GetContextAsync();

        var instructions = context.Instructions?.ReplaceLineEndings("\n");
        Assert.Contains("Known facts about the user:\n- [identity] User's name is Emre.", instructions);
        Assert.Contains("Facts about the user that may be relevant to this message:\n- [preference] User likes espresso.", instructions);
        Assert.Same(QueryVector, service.LastQueryVector);
    }

    [Fact]
    public async Task ProvideAIContextAsync_SkipsRelevantSearch_WhenQueryVectorIsMissing()
    {
        var service = new FakeMemoryItemService
        {
            CoreItems = [new MemoryItemSummary(1, "User's name is Emre.", "identity", true, DateTime.UtcNow)]
        };
        var provider = new TestMemoryItemContextProvider(service, queryVector: null);

        var context = await provider.GetContextAsync();

        Assert.Contains("Known facts about the user:", context.Instructions);
        Assert.DoesNotContain("may be relevant", context.Instructions);
        Assert.Null(service.LastQueryVector);
    }

    [Fact]
    public async Task ProvideAIContextAsync_ReturnsEmptyContext_WhenNothingIsFound()
    {
        var provider = new TestMemoryItemContextProvider(new FakeMemoryItemService(), QueryVector);

        var context = await provider.GetContextAsync();

        Assert.Null(context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_ReturnsEmptyContext_WhenMemoryLookupFails()
    {
        var provider = new TestMemoryItemContextProvider(new FakeMemoryItemService { Throw = true }, QueryVector);

        var context = await provider.GetContextAsync();

        Assert.Null(context.Instructions);
    }

    private sealed class TestMemoryItemContextProvider(IMemoryItemService memoryItemService, Vector? queryVector)
        : MemoryItemContextProvider(ChatId, memoryItemService, queryVector, NullLogger.Instance)
    {
        public ValueTask<AIContext> GetContextAsync()
        {
            return ProvideAIContextAsync(null!);
        }
    }

    private sealed class FakeMemoryItemService : IMemoryItemService
    {
        public IReadOnlyList<MemoryItemSummary> CoreItems { get; init; } = [];
        public IReadOnlyList<MemoryItemSearchResult> RelevantItems { get; init; } = [];
        public bool Throw { get; init; }
        public Vector? LastQueryVector { get; private set; }

        public Task<IReadOnlyList<MemoryItemSummary>> GetCoreItemsAsync(long chatId, CancellationToken cancellationToken)
        {
            return Throw
                ? throw new InvalidOperationException("database is down")
                : Task.FromResult(CoreItems);
        }

        public Task<IReadOnlyList<MemoryItemSearchResult>> SearchAsync(long chatId, Vector queryVector, CancellationToken cancellationToken)
        {
            LastQueryVector = queryVector;
            return Task.FromResult(RelevantItems);
        }

        public Task<IReadOnlyList<MemoryItemSummary>> GetActiveItemsAsync(long chatId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MemoryItemSearchResult>> FindNeighborsAsync(int telegramUserId, Vector vector, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> HasAnyItemsAsync(int telegramUserId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
