using System.Net;
using System.Net.Http.Json;
using Assistant.Api.Data;
using Assistant.Api.Features.Expense.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Tests.Expense;

public class ExpenseAnalysisServiceTests
{
    public static TheoryData<string, decimal, DateOnly, string, decimal, DateOnly, string, decimal> StatementCases => new()
    {
        {
            "ekstre-v1.md",
            28975.31m,
            new DateOnly(2025, 11, 25),
            "MODA ZÜCCACİYE BOSCH",
            9983.33m,
            new DateOnly(2025, 12, 8),
            "Microsoft*Xbox",
            2609.99m
        },
        {
            "ekstre-v2.md",
            32588.85m,
            new DateOnly(2026, 1, 14),
            "IYZICO *AMAZON.COM.T",
            1499.68m,
            new DateOnly(2026, 1, 15),
            "Google YouTube Super",
            110.00m
        },
        {
            "ekstre-v3.md",
            12614.85m,
            new DateOnly(2026, 2, 14),
            "IYZICO *AMAZON.COM.T",
            1499.66m,
            new DateOnly(2026, 2, 12),
            "VEGAN PAZARYERI",
            4.83m
        }
    };

    [Theory]
    [MemberData(nameof(StatementCases))]
    public void ParseStatementMarkdown_ExtractsExpensesAndTotal(
        string fixtureName,
        decimal expectedTotal,
        DateOnly firstExpectedDate,
        string firstExpectedName,
        decimal firstExpectedPrice,
        DateOnly secondExpectedDate,
        string secondExpectedName,
        decimal secondExpectedPrice)
    {
        var markdown = File.ReadAllText(GetFixturePath(fixtureName));

        var result = ExpenseAnalysisService.ParseStatementMarkdown(markdown);
        var diagnostic = BuildDiagnostic(result);

        Assert.True(result.Expenses.Count > 0, diagnostic);
        Assert.True(result.Total == expectedTotal, diagnostic);
        Assert.Contains(result.Expenses, expense =>
            expense.Date == firstExpectedDate &&
            expense.Name == firstExpectedName &&
            expense.Price == firstExpectedPrice);
        Assert.Contains(result.Expenses, expense =>
            expense.Date == secondExpectedDate &&
            expense.Name == secondExpectedName &&
            expense.Price == secondExpectedPrice);
    }

    [Fact]
    public async Task AnalyzeStatementAsync_PersistsEachParsedExpenseAsSeparateRow()
    {
        var markdown = File.ReadAllText(GetFixturePath("ekstre-v3.md"));
        await using var dbContext = CreateDbContext(nameof(AnalyzeStatementAsync_PersistsEachParsedExpenseAsSeparateRow));
        var service = CreateService(dbContext, markdown);

        var result = await service.AnalyzeStatementAsync(new MemoryStream([1, 2, 3]), 123L, 42, CancellationToken.None);
        var savedExpenses = await dbContext.Expenses
            .OrderBy(x => x.ExpenseDate)
            .ThenBy(x => x.Description)
            .ToListAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ParsedStatement);
        Assert.NotNull(result.Expenses);
        Assert.Equal(result.ParsedStatement!.Expenses.Count, savedExpenses.Count);
        Assert.Equal(savedExpenses.Count, result.Expenses!.Count);
        Assert.Contains("işlem kaydedildi", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.ParsedStatement.Total, savedExpenses.Sum(x => x.Amount));
        Assert.Single(savedExpenses.Select(x => x.StatementFingerprint).Distinct());
        Assert.All(savedExpenses, expense => Assert.False(string.IsNullOrWhiteSpace(expense.StatementFingerprint)));
        Assert.Contains(savedExpenses, expense =>
            expense.ExpenseDate == ToUtcDate(2026, 2, 14) &&
            expense.Description == "IYZICO *AMAZON.COM.T" &&
            expense.Amount == 1499.66m);
    }

    [Fact]
    public async Task AnalyzeStatementAsync_DoesNotInsertDuplicates_ForSameStatement()
    {
        var markdown = File.ReadAllText(GetFixturePath("ekstre-v1.md"));
        await using var dbContext = CreateDbContext(nameof(AnalyzeStatementAsync_DoesNotInsertDuplicates_ForSameStatement));
        var service = CreateService(dbContext, markdown);

        var firstResult = await service.AnalyzeStatementAsync(new MemoryStream([1]), 123L, 99, CancellationToken.None);
        var firstCount = await dbContext.Expenses.CountAsync();
        var secondResult = await service.AnalyzeStatementAsync(new MemoryStream([2]), 123L, 99, CancellationToken.None);
        var secondCount = await dbContext.Expenses.CountAsync();

        Assert.True(firstResult.IsSuccess);
        Assert.True(secondResult.IsSuccess, secondResult.UserMessage);
        Assert.Equal(firstCount, secondCount);
        Assert.Contains("zaten içeri aktarılmış", secondResult.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(secondResult.Expenses);
        Assert.Equal(firstCount, secondResult.Expenses!.Count);
        Assert.Single(secondResult.Expenses.Select(x => x.StatementFingerprint).Distinct());
    }

    [Fact]
    public void ExpenseModel_UsesNonUniqueFingerprintLookupIndex()
    {
        using var dbContext = CreateDbContext(nameof(ExpenseModel_UsesNonUniqueFingerprintLookupIndex));

        var entityType = dbContext.Model.FindEntityType(typeof(ExpenseModel));
        var index = entityType!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(["TelegramUserId", "StatementFingerprint"]));

        Assert.False(index.IsUnique);
    }

    private static string GetFixturePath(string fixtureName)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Assistant.Api.Tests",
            "Fixtures",
            "Ekstreler",
            fixtureName));
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static ExpenseAnalysisService CreateService(ApplicationDbContext dbContext, string markdown)
    {
        return new ExpenseAnalysisService(
            new StubHttpClientFactory(markdown),
            dbContext,
            NullLogger<ExpenseAnalysisService>.Instance);
    }

    private static DateTime ToUtcDate(int year, int month, int day)
    {
        return DateTime.SpecifyKind(new DateTime(year, month, day), DateTimeKind.Utc);
    }

    private static string BuildDiagnostic(ParsedExpenseStatement result)
    {
        var sample = string.Join(
            " | ",
            result.Expenses
                .Take(12)
                .Select(expense => $"{expense.Date:yyyy-MM-dd}:{expense.Name}:{expense.Price}"));

        var largest = string.Join(
            " | ",
            result.Expenses
                .OrderByDescending(expense => expense.Price)
                .Take(12)
                .Select(expense => $"{expense.Date:yyyy-MM-dd}:{expense.Name}:{expense.Price}"));

        var suspicious = string.Join(
            " | ",
            result.Expenses
                .Where(expense =>
                    expense.Price <= 6m ||
                    expense.Name.Contains("bonus", StringComparison.OrdinalIgnoreCase) ||
                    expense.Name.Contains("toplam", StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .Select(expense => $"{expense.Date:yyyy-MM-dd}:{expense.Name}:{expense.Price}"));

        return $"Count={result.Expenses.Count}; Total={result.Total}; Sample={sample}; Largest={largest}; Suspicious={suspicious}";
    }

    private sealed class StubHttpClientFactory(string markdown) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new MarkdownHttpMessageHandler(markdown))
            {
                BaseAddress = new Uri("http://localhost")
            };
        }
    }

    private sealed class MarkdownHttpMessageHandler(string markdown) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new MarkitdownConvertResponse("statement.md", markdown))
            });
        }
    }
}
