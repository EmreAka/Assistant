using System.Text.Json;
using Assistant.Api.Data;
using Assistant.Api.Features.Expense.Services;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Tests.Expense;

public class ExpenseQueryToolFunctionsTests
{
    [Fact]
    public async Task QueryExpenses_ReturnsOnlyCurrentUsersRows()
    {
        await using var dbContext = CreateDbContext(nameof(QueryExpenses_ReturnsOnlyCurrentUsersRows));
        SeedUserWithExpenses(dbContext, 1, 1001, [
            CreateExpense(1, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), 120m, "MARKET"),
            CreateExpense(1, new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), 90m, "COFFEE")
        ]);
        SeedUserWithExpenses(dbContext, 2, 2002, [
            CreateExpense(2, new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc), 999m, "OTHER USER")
        ]);

        var tool = new ExpenseQueryToolFunctions(1001, dbContext, NullLogger<ExpenseQueryToolFunctions>.Instance);

        var json = await tool.QueryExpenses(limit: 10, summaryMode: "list");
        var response = Deserialize(json);

        Assert.True(response.IsSuccess, response.Message);
        Assert.Equal(2, response.TransactionCount);
        Assert.NotNull(response.Items);
        Assert.Equal(2, response.Items!.Count);
        Assert.DoesNotContain(response.Items, x => x.Description == "OTHER USER");
    }

    [Fact]
    public async Task QueryExpenses_AggregatesByDescription_AndAppliesDateFilters()
    {
        await using var dbContext = CreateDbContext(nameof(QueryExpenses_AggregatesByDescription_AndAppliesDateFilters));
        SeedUserWithExpenses(dbContext, 1, 1001, [
            CreateExpense(1, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc), 25m, "COFFEE"),
            CreateExpense(1, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), 100m, "MARKET"),
            CreateExpense(1, new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), 80m, "MARKET"),
            CreateExpense(1, new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc), 50m, "COFFEE")
        ]);

        var tool = new ExpenseQueryToolFunctions(1001, dbContext, NullLogger<ExpenseQueryToolFunctions>.Instance);

        var json = await tool.QueryExpenses(
            startDate: "2026-03-01",
            endDate: "2026-03-31",
            groupBy: "description",
            summaryMode: "aggregate",
            sortBy: "total_desc",
            limit: 10);

        var response = Deserialize(json);

        Assert.True(response.IsSuccess, response.Message);
        Assert.Equal(3, response.TransactionCount);
        Assert.Equal(230m, response.TotalAmount);
        Assert.NotNull(response.Groups);
        Assert.Equal(2, response.Groups!.Count);
        Assert.Equal("MARKET", response.Groups[0].GroupKey);
        Assert.Equal(180m, response.Groups[0].TotalAmount);
        Assert.Equal("COFFEE", response.Groups[1].GroupKey);
        Assert.Equal(50m, response.Groups[1].TotalAmount);
    }

    [Fact]
    public async Task QueryExpenses_FiltersByCategory_WithNormalizedExactMatch()
    {
        await using var dbContext = CreateDbContext(nameof(QueryExpenses_FiltersByCategory_WithNormalizedExactMatch));
        SeedUserWithExpenses(dbContext, 1, 1001, [
            CreateExpense(1, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), 120m, "DINNER", "Food & Dining"),
            CreateExpense(1, new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), 80m, "COFFEE", "Food & Dining"),
            CreateExpense(1, new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc), 250m, "AI TOOL", "Subscriptions & Software")
        ]);

        var tool = new ExpenseQueryToolFunctions(1001, dbContext, NullLogger<ExpenseQueryToolFunctions>.Instance);

        var json = await tool.QueryExpenses(
            category: "  food    &    dining  ",
            limit: 10,
            summaryMode: "list",
            sortBy: "date_asc");

        var response = Deserialize(json);

        Assert.True(response.IsSuccess, response.Message);
        Assert.Equal(2, response.TransactionCount);
        Assert.Equal(200m, response.TotalAmount);
        Assert.NotNull(response.Items);
        Assert.Equal(2, response.Items!.Count);
        Assert.All(response.Items, x => Assert.Equal("Food & Dining", x.Category));
        Assert.DoesNotContain(response.Items, x => x.Description == "AI TOOL");
    }

    [Fact]
    public async Task QueryExpenses_AggregatesByCategory()
    {
        await using var dbContext = CreateDbContext(nameof(QueryExpenses_AggregatesByCategory));
        SeedUserWithExpenses(dbContext, 1, 1001, [
            CreateExpense(1, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), 100m, "MARKET", "Shopping"),
            CreateExpense(1, new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), 80m, "STORE", "Shopping"),
            CreateExpense(1, new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc), 50m, "COFFEE", "Food & Dining"),
            CreateExpense(1, new DateTime(2026, 3, 13, 0, 0, 0, DateTimeKind.Utc), 200m, "AI TOOL", "Subscriptions & Software")
        ]);

        var tool = new ExpenseQueryToolFunctions(1001, dbContext, NullLogger<ExpenseQueryToolFunctions>.Instance);

        var json = await tool.QueryExpenses(
            groupBy: "category",
            summaryMode: "aggregate",
            sortBy: "total_desc",
            limit: 10);

        var response = Deserialize(json);

        Assert.True(response.IsSuccess, response.Message);
        Assert.Equal("category", response.GroupBy);
        Assert.Equal(4, response.TransactionCount);
        Assert.Equal(430m, response.TotalAmount);
        Assert.NotNull(response.Groups);
        Assert.Equal(3, response.Groups!.Count);
        Assert.Equal("Subscriptions & Software", response.Groups[0].GroupKey);
        Assert.Equal(1, response.Groups[0].TransactionCount);
        Assert.Equal(200m, response.Groups[0].TotalAmount);
        Assert.Equal("Shopping", response.Groups[1].GroupKey);
        Assert.Equal(2, response.Groups[1].TransactionCount);
        Assert.Equal(180m, response.Groups[1].TotalAmount);
        Assert.Equal("Food & Dining", response.Groups[2].GroupKey);
        Assert.Equal(50m, response.Groups[2].TotalAmount);
    }

    [Fact]
    public async Task QueryExpenses_ReturnsNotFound_WhenChatHasNoRegisteredUser()
    {
        await using var dbContext = CreateDbContext(nameof(QueryExpenses_ReturnsNotFound_WhenChatHasNoRegisteredUser));
        var tool = new ExpenseQueryToolFunctions(4040, dbContext, NullLogger<ExpenseQueryToolFunctions>.Instance);

        var json = await tool.QueryExpenses(summaryMode: "list");
        var response = Deserialize(json);

        Assert.False(response.IsSuccess);
        Assert.Equal("User not found for this chat.", response.Message);
    }

    private static ExpenseQueryResponse Deserialize(string json)
    {
        return JsonSerializer.Deserialize<ExpenseQueryResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static void SeedUserWithExpenses(ApplicationDbContext dbContext, int userId, long chatId, IEnumerable<ExpenseModel> expenses)
    {
        dbContext.TelegramUsers.Add(new TelegramUser
        {
            Id = userId,
            ChatId = chatId,
            CreatedAt = DateTime.UtcNow,
            FirstName = $"User{userId}",
            UserName = $"user{userId}"
        });
        dbContext.Expenses.AddRange(expenses);
        dbContext.SaveChanges();
    }

    private static ExpenseModel CreateExpense(
        int userId,
        DateTime expenseDate,
        decimal amount,
        string description,
        string category = "Other")
    {
        return new ExpenseModel
        {
            TelegramUserId = userId,
            ExpenseDate = expenseDate,
            Amount = amount,
            Currency = "TRY",
            Description = description,
            StatementFingerprint = Guid.NewGuid().ToString("N"),
            Category = category,
            CreatedAt = DateTime.UtcNow
        };
    }
}
