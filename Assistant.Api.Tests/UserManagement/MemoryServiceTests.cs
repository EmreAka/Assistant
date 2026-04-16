using Assistant.Api.Data;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryServiceTests
{
    [Fact]
    public async Task GetActiveManifestAsync_ReturnsEmptyString_WhenNoManifestExists()
    {
        await using var dbContext = CreateDbContext(nameof(GetActiveManifestAsync_ReturnsEmptyString_WhenNoManifestExists));
        SeedUser(dbContext, 1, 1001);
        var service = new MemoryService(dbContext);

        var result = await service.GetActiveManifestAsync(1001, CancellationToken.None);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task GetActiveManifestRecordAsync_ReturnsLatestActiveManifest()
    {
        await using var dbContext = CreateDbContext(nameof(GetActiveManifestRecordAsync_ReturnsLatestActiveManifest));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemoryManifests.Add(CreateManifest(1, "Old manifest", 1, isActive: false));
        dbContext.UserMemoryManifests.Add(CreateManifest(1, "Current manifest", 2, isActive: true));
        await dbContext.SaveChangesAsync();
        var service = new MemoryService(dbContext);

        var manifest = await service.GetActiveManifestRecordAsync(1001, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("Current manifest", manifest.Content);
        Assert.Equal(2, manifest.Version);
        Assert.True(manifest.IsActive);
    }

    [Fact]
    public async Task SaveManifestAsync_CreatesInitialActiveManifest()
    {
        await using var dbContext = CreateDbContext(nameof(SaveManifestAsync_CreatesInitialActiveManifest));
        SeedUser(dbContext, 1, 1001);
        var service = new MemoryService(dbContext);

        var saved = await service.SaveManifestAsync(1001, "User likes espresso.", CancellationToken.None);

        var storedManifest = await dbContext.UserMemoryManifests.SingleAsync();
        Assert.True(saved);
        Assert.Equal("User likes espresso.", storedManifest.Content);
        Assert.Equal(1, storedManifest.Version);
        Assert.True(storedManifest.IsActive);
    }

    [Fact]
    public async Task SaveManifestAsync_DeactivatesPreviousManifest_AndReturnsLatestActiveContent()
    {
        await using var dbContext = CreateDbContext(nameof(SaveManifestAsync_DeactivatesPreviousManifest_AndReturnsLatestActiveContent));
        SeedUser(dbContext, 1, 1001);
        dbContext.UserMemoryManifests.Add(CreateManifest(1, "Old manifest", 1, isActive: true));
        await dbContext.SaveChangesAsync();

        var service = new MemoryService(dbContext);

        var saved = await service.SaveManifestAsync(1001, "Updated manifest", CancellationToken.None);
        var activeManifest = await service.GetActiveManifestAsync(1001, CancellationToken.None);
        var storedManifests = await dbContext.UserMemoryManifests
            .Where(x => x.TelegramUserId == 1)
            .OrderBy(x => x.Version)
            .ToListAsync();

        Assert.True(saved);
        Assert.Equal("Updated manifest", activeManifest);
        Assert.Equal(2, storedManifests.Count);
        Assert.False(storedManifests[0].IsActive);
        Assert.True(storedManifests[1].IsActive);
        Assert.Equal(2, storedManifests[1].Version);
    }

    [Fact]
    public async Task SaveManifestAsync_ReturnsFalse_WhenUserDoesNotExist()
    {
        await using var dbContext = CreateDbContext(nameof(SaveManifestAsync_ReturnsFalse_WhenUserDoesNotExist));
        var service = new MemoryService(dbContext);

        var saved = await service.SaveManifestAsync(1001, "Manifest content", CancellationToken.None);

        Assert.False(saved);
        Assert.Empty(await dbContext.UserMemoryManifests.ToListAsync());
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
