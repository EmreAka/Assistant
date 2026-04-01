using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Expense.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Tests.Expense;

/// <summary>
/// ExpenseAnalysisService now calls Google Gen AI directly (no HTTP stubbable surface).
/// The old ParseStatementMarkdown / IHttpClientFactory-based tests were removed because
/// the extraction path no longer uses an injectable HTTP client.
/// Integration tests with real API keys would cover PDF→expense round-trip.
/// </summary>
public class ExpenseAnalysisServiceTests
{
    [Fact]
    public void ExpenseModel_UsesNonUniqueFingerprintLookupIndex()
    {
        using var dbContext = CreateDbContext(nameof(ExpenseModel_UsesNonUniqueFingerprintLookupIndex));

        var entityType = dbContext.Model.FindEntityType(typeof(ExpenseModel));
        var index = entityType!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(["TelegramUserId", "StatementFingerprint"]));

        Assert.False(index.IsUnique);
    }

    [Fact]
    public void ExpenseModel_CategoryFieldExistsOnEntityType()
    {
        using var dbContext = CreateDbContext(nameof(ExpenseModel_CategoryFieldExistsOnEntityType));

        var entityType = dbContext.Model.FindEntityType(typeof(ExpenseModel));
        var categoryProperty = entityType!.FindProperty("Category");

        Assert.NotNull(categoryProperty);
        Assert.Equal(typeof(string), categoryProperty.ClrType);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new ApplicationDbContext(options);
    }
}
