using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.UserManagement;

// Only the non-vector queries: InMemory can't run CosineDistance.
public class MemoryItemServiceTests
{
    [Fact]
    public async Task GetCoreItemsAsync_ReturnsActiveCoreItemsOfTheChat_UpToMaxCoreItems()
    {
        await using var dbContext = CreateDbContext(nameof(GetCoreItemsAsync_ReturnsActiveCoreItemsOfTheChat_UpToMaxCoreItems));
        SeedUser(dbContext, 1, 1001);
        SeedUser(dbContext, 2, 2002);
        SeedItem(dbContext, 1, "c", UserMemoryItemCategories.Preference, isCore: true);
        SeedItem(dbContext, 1, "a", UserMemoryItemCategories.Identity, isCore: true);
        SeedItem(dbContext, 1, "b", UserMemoryItemCategories.Identity, isCore: true);
        SeedItem(dbContext, 1, "non-core", UserMemoryItemCategories.Identity, isCore: false);
        SeedItem(dbContext, 1, "superseded", UserMemoryItemCategories.Identity, isCore: true, UserMemoryItemStatuses.Superseded);
        SeedItem(dbContext, 2, "other user", UserMemoryItemCategories.Identity, isCore: true);
        var service = CreateService(dbContext, maxCoreItems: 2);

        var items = await service.GetCoreItemsAsync(1001, CancellationToken.None);

        Assert.Equal(["a", "b"], items.Select(x => x.Text));
    }

    [Fact]
    public async Task GetActiveItemsAsync_ReturnsCoreFirstThenByCategory()
    {
        await using var dbContext = CreateDbContext(nameof(GetActiveItemsAsync_ReturnsCoreFirstThenByCategory));
        SeedUser(dbContext, 1, 1001);
        SeedItem(dbContext, 1, "preference", UserMemoryItemCategories.Preference, isCore: false);
        SeedItem(dbContext, 1, "goal", UserMemoryItemCategories.Goal, isCore: false);
        SeedItem(dbContext, 1, "core", UserMemoryItemCategories.Preference, isCore: true);
        SeedItem(dbContext, 1, "deleted", UserMemoryItemCategories.Goal, isCore: false, UserMemoryItemStatuses.Deleted);
        var service = CreateService(dbContext);

        var items = await service.GetActiveItemsAsync(1001, CancellationToken.None);

        Assert.Equal(["core", "goal", "preference"], items.Select(x => x.Text));
    }

    [Fact]
    public async Task HasAnyItemsAsync_CountsItemsInAnyStatus()
    {
        await using var dbContext = CreateDbContext(nameof(HasAnyItemsAsync_CountsItemsInAnyStatus));
        SeedUser(dbContext, 1, 1001);
        SeedUser(dbContext, 2, 2002);
        SeedItem(dbContext, 1, "deleted", UserMemoryItemCategories.Other, isCore: false, UserMemoryItemStatuses.Deleted);
        var service = CreateService(dbContext);

        Assert.True(await service.HasAnyItemsAsync(1, CancellationToken.None));
        Assert.False(await service.HasAnyItemsAsync(2, CancellationToken.None));
    }

    private static MemoryItemService CreateService(ApplicationDbContext dbContext, int maxCoreItems = 40)
    {
        return new MemoryItemService(
            dbContext,
            Options.Create(new MemoryItemOptions { MaxCoreItems = maxCoreItems }),
            NullLogger<MemoryItemService>.Instance);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            // Prefixed: InMemory database names are shared across all test classes.
            .UseInMemoryDatabase($"{nameof(MemoryItemServiceTests)}.{databaseName}")
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

    private static void SeedItem(
        ApplicationDbContext dbContext,
        int telegramUserId,
        string text,
        string category,
        bool isCore,
        string status = UserMemoryItemStatuses.Active)
    {
        dbContext.UserMemoryItems.Add(new UserMemoryItem
        {
            TelegramUserId = telegramUserId,
            Text = text,
            Category = category,
            IsCore = isCore,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastConfirmedAt = DateTime.UtcNow
        });
        dbContext.SaveChanges();
    }
}
