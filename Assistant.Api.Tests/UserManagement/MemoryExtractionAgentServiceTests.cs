using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryExtractionAgentServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 4, 19, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExtractFromTurnsAsync_SendsDelimitedTurnsWithLocalTime_AndParsesFacts()
    {
        var chatClient = new FakeChatClient("""
                                            {"facts":[{"text":"User likes espresso.","category":"preference","isCore":false,"sourceTurnIds":[12]}]}
                                            """);
        var service = CreateService(chatClient);

        var facts = await service.ExtractFromTurnsAsync(
            [new MemoryExtractionTurn(12, "I love espresso", "Nice!", new DateTime(2026, 4, 18, 22, 30, 0, DateTimeKind.Utc))],
            CancellationToken.None);

        var input = chatClient.LastInput.ReplaceLineEndings("\n");
        Assert.Contains("""<turn id="12" at="2026-04-19 01:30 Europe/Istanbul">""", input);
        Assert.Contains("<user>I love espresso</user>", input);
        Assert.Contains("<assistant>Nice!</assistant>", input);
        Assert.Contains("At most 300 characters per fact.", chatClient.LastOptions?.Instructions);
        Assert.Contains(string.Join(", ", UserMemoryItemCategories.All), chatClient.LastOptions?.Instructions);
        Assert.Equal(0.2f, chatClient.LastOptions?.Temperature);

        var fact = Assert.Single(facts);
        Assert.Equal("User likes espresso.", fact.Text);
        Assert.Equal("preference", fact.Category);
        Assert.False(fact.IsCore);
        Assert.Equal([12], fact.SourceTurnIds);
    }

    [Fact]
    public async Task ReconcileAsync_ListsNeighborsUnderEachCandidate_AndParsesDecisions()
    {
        var chatClient = new FakeChatClient("""
                                            {"decisions":[{"candidateIndex":0,"action":"update","targetItemId":5,"text":"User lives in Ankara.","category":"identity","isCore":false,"reason":"moved"}]}
                                            """);
        var service = CreateService(chatClient);

        var decisions = await service.ReconcileAsync(
            [
                new ReconcileCandidate(
                    0,
                    new CandidateFact("User lives in Ankara.", "identity", false, []),
                    [new MemoryItemSearchResult(5, "User lives in Istanbul.", "identity", false, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), 0.2)])
            ],
            CancellationToken.None);

        var input = chatClient.LastInput;
        Assert.Contains("""<candidate index="0" category="identity" isCore="false">""", input);
        Assert.Contains("""<existing id="5" category="identity" isCore="false" created="2026-03-01">User lives in Istanbul.</existing>""", input);
        Assert.Equal(
            new MemoryDecision(0, "update", 5, "User lives in Ankara.", "identity", false, "moved"),
            Assert.Single(decisions));
    }

    [Fact]
    public async Task EmptyInputs_DoNotCallTheModel()
    {
        var chatClient = new FakeChatClient("{}");
        var service = CreateService(chatClient);

        Assert.Empty(await service.ExtractFromTurnsAsync([], CancellationToken.None));
        Assert.Empty(await service.ExtractFromManifestAsync("  ", CancellationToken.None));
        Assert.Empty(await service.ReconcileAsync([], CancellationToken.None));
        Assert.Equal(0, chatClient.CallCount);
    }

    private static MemoryExtractionAgentService CreateService(IChatClient chatClient)
    {
        var aiOptions = Options.Create(new AiProvidersOptions { DefaultTimeZoneId = "Europe/Istanbul" });

        return new MemoryExtractionAgentService(
            chatClient,
            new AssistantTimeService(aiOptions, new FixedTimeProvider(FixedUtcNow)),
            aiOptions,
            Options.Create(new MemoryItemOptions { MaxItemLength = 300 }));
    }

    private sealed class FakeChatClient(string responseJson) : IChatClient
    {
        public string LastInput { get; private set; } = string.Empty;
        public ChatOptions? LastOptions { get; private set; }
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastInput = string.Join("\n", messages.Select(x => x.Text));
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
