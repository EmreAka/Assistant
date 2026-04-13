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
    public async Task UpdateMemoryManifest_SavesManifestForCurrentChat()
    {
        await using var dbContext = CreateDbContext(nameof(UpdateMemoryManifest_SavesManifestForCurrentChat));
        SeedUser(dbContext, 1, 1001);
        var tool = CreateTool(1001, dbContext);

        var result = await tool.UpdateMemoryManifest("User prefers filter coffee.");

        var storedManifest = await dbContext.UserMemoryManifests.SingleAsync();
        Assert.Equal("Memory manifest updated successfully.", result);
        Assert.Equal("User prefers filter coffee.", storedManifest.Content);
        Assert.Equal(1, storedManifest.Version);
        Assert.True(storedManifest.IsActive);
    }

    [Fact]
    public async Task UpdateMemoryManifest_CreatesNewActiveVersion_WithoutTouchingOtherChats()
    {
        await using var dbContext = CreateDbContext(nameof(UpdateMemoryManifest_CreatesNewActiveVersion_WithoutTouchingOtherChats));
        SeedUser(dbContext, 1, 1001);
        SeedUser(dbContext, 2, 2002);
        dbContext.UserMemoryManifests.AddRange(
            CreateManifest(1, "Old chat 1001 manifest", 1, isActive: true),
            CreateManifest(2, "Chat 2002 manifest", 1, isActive: true));
        await dbContext.SaveChangesAsync();

        var tool = CreateTool(1001, dbContext);

        var result = await tool.UpdateMemoryManifest("Updated chat 1001 manifest");

        var chat1001Manifests = await dbContext.UserMemoryManifests
            .Where(x => x.TelegramUserId == 1)
            .OrderBy(x => x.Version)
            .ToListAsync();
        var chat2002Manifest = await dbContext.UserMemoryManifests
            .SingleAsync(x => x.TelegramUserId == 2);

        Assert.Equal("Memory manifest updated successfully.", result);
        Assert.Equal(2, chat1001Manifests.Count);
        Assert.False(chat1001Manifests[0].IsActive);
        Assert.True(chat1001Manifests[1].IsActive);
        Assert.Equal("Updated chat 1001 manifest", chat1001Manifests[1].Content);
        Assert.Equal(2, chat1001Manifests[1].Version);
        Assert.True(chat2002Manifest.IsActive);
        Assert.Equal("Chat 2002 manifest", chat2002Manifest.Content);
    }

    [Fact]
    public async Task UpdateMemoryManifest_ReturnsFailure_WhenChatIsUnknown()
    {
        await using var dbContext = CreateDbContext(nameof(UpdateMemoryManifest_ReturnsFailure_WhenChatIsUnknown));
        var tool = CreateTool(1001, dbContext);

        var result = await tool.UpdateMemoryManifest("Anything");

        Assert.Equal("Failed to update memory manifest.", result);
        Assert.Empty(await dbContext.UserMemoryManifests.ToListAsync());
    }

    private static MemoryToolFunctions CreateTool(long chatId, ApplicationDbContext dbContext)
    {
        return new MemoryToolFunctions(
            chatId,
            new MemoryService(dbContext),
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
