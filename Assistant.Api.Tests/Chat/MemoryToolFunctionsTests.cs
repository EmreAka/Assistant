using Assistant.Api.Data;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Api.Tests.ChatFeatures;

public class MemoryToolFunctionsTests
{
    [Fact]
    public async Task ListMemories_ReturnsOnlyActiveMemoriesByDefault()
    {
        await using var dbContext = CreateDbContext(nameof(ListMemories_ReturnsOnlyActiveMemoriesByDefault));
        SeedUser(dbContext, 1, 1001);
        SeedUser(dbContext, 2, 2002);
        dbContext.UserMemories.AddRange(
            CreateMemory(1, "preference", "User likes filter coffee", 8),
            CreateMemory(1, "fact", "User is on vacation", 6, expiresAtUtc: DateTime.UtcNow.AddHours(-1)),
            CreateMemory(2, "goal", "Other chat goal", 7));
        await dbContext.SaveChangesAsync();

        var tool = CreateTool(1001, dbContext);

        var result = await tool.ListMemories();

        Assert.Contains("Memories for current chat:", result);
        Assert.Contains("Memory ID:", result);
        Assert.Contains("User likes filter coffee", result);
        Assert.DoesNotContain("User is on vacation", result);
        Assert.DoesNotContain("Other chat goal", result);
    }

    [Fact]
    public async Task UpdateMemory_UpdatesFields_AndCanClearExpiration()
    {
        await using var dbContext = CreateDbContext(nameof(UpdateMemory_UpdatesFields_AndCanClearExpiration));
        SeedUser(dbContext, 1, 1001);
        var memory = CreateMemory(1, "preference", "User likes latte", 5, expiresAtUtc: DateTime.UtcNow.AddDays(2));
        dbContext.UserMemories.Add(memory);
        await dbContext.SaveChangesAsync();

        var tool = CreateTool(1001, dbContext);

        var result = await tool.UpdateMemory(memory.Id, content: "User likes espresso", importance: 9, clearExpiration: true);

        var storedMemory = await dbContext.UserMemories.SingleAsync();
        Assert.Equal($"Memory updated successfully. Memory ID: {memory.Id}", result);
        Assert.Equal("User likes espresso", storedMemory.Content);
        Assert.Equal(9, storedMemory.Importance);
        Assert.Null(storedMemory.ExpiresAt);
    }

    [Fact]
    public async Task DeleteMemory_RemovesMemoryForCurrentChat()
    {
        await using var dbContext = CreateDbContext(nameof(DeleteMemory_RemovesMemoryForCurrentChat));
        SeedUser(dbContext, 1, 1001);
        var memory = CreateMemory(1, "fact", "User works remotely", 7);
        dbContext.UserMemories.Add(memory);
        await dbContext.SaveChangesAsync();

        var tool = CreateTool(1001, dbContext);

        var result = await tool.DeleteMemory(memory.Id);

        Assert.Equal($"Memory deleted successfully. Memory ID: {memory.Id}", result);
        Assert.Empty(await dbContext.UserMemories.ToListAsync());
    }

    private static MemoryToolFunctions CreateTool(long chatId, ApplicationDbContext dbContext)
    {
        return new MemoryToolFunctions(
            chatId,
            "Europe/Istanbul",
            new MemoryService(dbContext, NullLogger<MemoryService>.Instance),
            NullLogger<MemoryToolFunctions>.Instance);
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
