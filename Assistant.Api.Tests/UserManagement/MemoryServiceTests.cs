using Assistant.Api.Data;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryServiceTests
{
    [Fact]
    public async Task SearchActiveMemoriesAsync_ReturnsOnlyRelevantMatches()
    {
        await using var dbContext = CreateDbContext(nameof(SearchActiveMemoriesAsync_ReturnsOnlyRelevantMatches));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemories.AddRange(
            CreateMemory(1, "preference", "User likes filter coffee", 8),
            CreateMemory(1, "goal", "User wants to finish the C# course", 7),
            CreateMemory(1, "fact", "User has two cats", 6));
        await dbContext.SaveChangesAsync();

        var service = new MemoryService(dbContext, NullLogger<MemoryService>.Instance);

        var result = await service.SearchActiveMemoriesAsync(1001, "coffee beans", 5, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("User likes filter coffee", result[0].Content);
    }

    [Fact]
    public async Task SearchRecentExpiredTimeBoundMemoriesAsync_ReturnsRecentRelevantExpiredMemoriesOnly()
    {
        await using var dbContext = CreateDbContext(nameof(SearchRecentExpiredTimeBoundMemoriesAsync_ReturnsRecentRelevantExpiredMemoriesOnly));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemories.AddRange(
            CreateMemory(1, "fact", "User was traveling in Rome", 7, expiresAtUtc: DateTime.UtcNow.AddDays(-2)),
            CreateMemory(1, "fact", "User was in Berlin", 7, expiresAtUtc: DateTime.UtcNow.AddDays(-45)),
            CreateMemory(1, "goal", "User wants to practice piano", 8));
        await dbContext.SaveChangesAsync();

        var service = new MemoryService(dbContext, NullLogger<MemoryService>.Instance);

        var result = await service.SearchRecentExpiredTimeBoundMemoriesAsync(1001, "travel Rome", 5, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("User was traveling in Rome", result[0].Content);
    }

    [Fact]
    public async Task ListMemoriesAsync_AllFilterAndSearchText_ReturnsMatchingMemories()
    {
        await using var dbContext = CreateDbContext(nameof(ListMemoriesAsync_AllFilterAndSearchText_ReturnsMatchingMemories));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemories.AddRange(
            CreateMemory(1, "preference", "User likes espresso", 8),
            CreateMemory(1, "project", "Assistant project deadline is Friday", 9),
            CreateMemory(1, "fact", "User is temporarily in Ankara", 5, expiresAtUtc: DateTime.UtcNow.AddHours(-2)));
        await dbContext.SaveChangesAsync();

        var service = new MemoryService(dbContext, NullLogger<MemoryService>.Instance);

        var result = await service.ListMemoriesAsync(1001, "all", "project", 10, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Assistant project deadline is Friday", result[0].Content);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new TestApplicationDbContext(options);
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

    private static UserMemory CreateMemory(int telegramUserId, string category, string content, int importance, DateTime? expiresAtUtc = null)
    {
        return new UserMemory
        {
            TelegramUserId = telegramUserId,
            Category = category,
            Content = content,
            Importance = importance,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = expiresAtUtc
        };
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
