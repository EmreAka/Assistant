using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.ChatFeatures;

public class TemporalContextProviderTests
{
    private const long ChatId = 1001;
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 4, 19, 15, 1, 22, TimeSpan.Zero);

    [Fact]
    public async Task ProvideAIContextAsync_IncludesCurrentAnchors_WhenNoPreviousTurnExists()
    {
        await using var dbContext = CreateDbContext(nameof(ProvideAIContextAsync_IncludesCurrentAnchors_WhenNoPreviousTurnExists));
        var provider = CreateProvider(dbContext);

        var context = await provider.GetContextAsync();

        Assert.Contains("- now_local: 2026-04-19 18:01:22 Europe/Istanbul", context.Instructions);
        Assert.Contains("- day_of_week: Sunday", context.Instructions);
        Assert.Contains("- day_period: evening", context.Instructions);
        Assert.Contains("- yesterday: 2026-04-18", context.Instructions);
        Assert.Contains("- today: 2026-04-19", context.Instructions);
        Assert.Contains("- tomorrow: 2026-04-20", context.Instructions);
        Assert.Contains("- tonight: 2026-04-19 evening/night", context.Instructions);
        Assert.Contains("- this_week: 2026-04-13..2026-04-19", context.Instructions);
        Assert.Contains("- next_week: 2026-04-20..2026-04-26", context.Instructions);
        Assert.Contains("- last_chat_activity_local: none", context.Instructions);
        Assert.Contains("- elapsed_since_last_chat_activity: none", context.Instructions);
        Assert.Contains("- conversation_pacing: no_previous_activity", context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_LabelsImmediateContinuation()
    {
        await using var dbContext = CreateDbContext(nameof(ProvideAIContextAsync_LabelsImmediateContinuation));
        SeedLastTurn(dbContext, FixedUtcNow.UtcDateTime.AddSeconds(-30));
        var provider = CreateProvider(dbContext);

        var context = await provider.GetContextAsync();

        Assert.Contains("- elapsed_since_last_chat_activity: less than 1 minute", context.Instructions);
        Assert.Contains("- conversation_pacing: immediate", context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_LabelsSameDayGap()
    {
        await using var dbContext = CreateDbContext(nameof(ProvideAIContextAsync_LabelsSameDayGap));
        SeedLastTurn(dbContext, FixedUtcNow.UtcDateTime.AddHours(-2).AddMinutes(-30));
        var provider = CreateProvider(dbContext);

        var context = await provider.GetContextAsync();

        Assert.Contains("- elapsed_since_last_chat_activity: 2 hours 30 minutes", context.Instructions);
        Assert.Contains("- conversation_pacing: same_day_gap", context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_LabelsLongOvernightGap()
    {
        await using var dbContext = CreateDbContext(nameof(ProvideAIContextAsync_LabelsLongOvernightGap));
        SeedLastTurn(dbContext, new DateTime(2026, 4, 18, 19, 32, 0, DateTimeKind.Utc));
        var provider = CreateProvider(dbContext);

        var context = await provider.GetContextAsync();

        Assert.Contains("- last_chat_activity_local: 2026-04-18 22:32:00 Europe/Istanbul", context.Instructions);
        Assert.Contains("- elapsed_since_last_chat_activity: 19 hours 29 minutes", context.Instructions);
        Assert.Contains("- conversation_pacing: long_gap", context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_LabelsMultiDayGap()
    {
        await using var dbContext = CreateDbContext(nameof(ProvideAIContextAsync_LabelsMultiDayGap));
        SeedLastTurn(dbContext, FixedUtcNow.UtcDateTime.AddDays(-3).AddHours(-1));
        var provider = CreateProvider(dbContext);

        var context = await provider.GetContextAsync();

        Assert.Contains("- elapsed_since_last_chat_activity: 3 days 1 hour", context.Instructions);
        Assert.Contains("- conversation_pacing: multi_day_gap", context.Instructions);
    }

    private static TestTemporalContextProvider CreateProvider(ApplicationDbContext dbContext)
    {
        return new TestTemporalContextProvider(ChatId, dbContext, CreateTimeService());
    }

    private static AssistantTimeService CreateTimeService()
    {
        return new AssistantTimeService(
            Options.Create(new AiProvidersOptions
            {
                DefaultTimeZoneId = "Europe/Istanbul"
            }),
            new FixedTimeProvider(FixedUtcNow));
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static void SeedLastTurn(ApplicationDbContext dbContext, DateTime createdAtUtc)
    {
        dbContext.TelegramUsers.Add(new TelegramUser
        {
            Id = 1,
            ChatId = ChatId,
            CreatedAt = DateTime.UtcNow
        });

        dbContext.ChatTurns.Add(new ChatTurn
        {
            TelegramUserId = 1,
            UserMessage = "hello",
            AssistantMessage = "hi",
            CreatedAt = createdAtUtc
        });

        dbContext.SaveChanges();
    }

    private sealed class TestTemporalContextProvider(
        long chatId,
        ApplicationDbContext dbContext,
        IAssistantTimeService assistantTimeService)
        : TemporalContextProvider(chatId, dbContext, assistantTimeService)
    {
        public ValueTask<AIContext> GetContextAsync()
        {
            return ProvideAIContextAsync(null!);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
