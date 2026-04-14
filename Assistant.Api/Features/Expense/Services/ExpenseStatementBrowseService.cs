using Assistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.Expense.Services;

public class ExpenseStatementBrowseService(ApplicationDbContext dbContext) : IExpenseStatementBrowseService
{
    public async Task<ExpenseStatementOverview?> GetOverviewAsync(int userId, CancellationToken cancellationToken)
    {
        var expenses = await dbContext.Expenses
            .AsNoTracking()
            .Where(x => x.TelegramUserId == userId)
            .OrderByDescending(x => x.ExpenseDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        if (expenses.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow.Date;
        var last30Start = now.AddDays(-30);
        var last30Total = expenses
            .Where(x => x.ExpenseDate.Date >= last30Start && x.ExpenseDate.Date <= now)
            .Sum(x => x.Amount);

        var statementPeriods = expenses
            .GroupBy(x => x.StatementFingerprint)
            .Select(g => new ExpenseStatementPeriod(
                g.Key,
                g.Min(x => x.ExpenseDate),
                g.Max(x => x.ExpenseDate),
                g.Count(),
                g.Sum(x => x.Amount),
                DetermineCurrencyLabel(g.Select(x => x.Currency))))
            .OrderByDescending(x => x.EndDate)
            .ThenByDescending(x => x.StartDate)
            .ThenBy(x => x.Fingerprint, StringComparer.Ordinal)
            .ToList();

        var categories = BuildCategorySummaries(expenses.Select(x => new ExpenseStatementTransaction(
            x.ExpenseDate,
            x.Amount,
            x.Currency,
            x.Description,
            x.Category)));

        return new ExpenseStatementOverview(
            expenses.Sum(x => x.Amount),
            expenses.Count,
            last30Total,
            expenses.Max(x => x.ExpenseDate),
            statementPeriods,
            categories);
    }

    public async Task<ExpenseStatementDetail?> GetStatementDetailAsync(
        int userId,
        string statementFingerprint,
        string? categoryFilter,
        CancellationToken cancellationToken)
    {
        var transactions = await dbContext.Expenses
            .AsNoTracking()
            .Where(x => x.TelegramUserId == userId && x.StatementFingerprint == statementFingerprint)
            .OrderBy(x => x.ExpenseDate)
            .ThenBy(x => x.Id)
            .Select(x => new ExpenseStatementTransaction(
                x.ExpenseDate,
                x.Amount,
                x.Currency,
                x.Description,
                x.Category))
            .ToListAsync(cancellationToken);

        if (transactions.Count == 0)
        {
            return null;
        }

        var categories = BuildCategorySummaries(transactions);
        var filteredTransactions = ApplyCategoryFilter(transactions, categoryFilter);

        return new ExpenseStatementDetail(
            statementFingerprint,
            transactions.Min(x => x.ExpenseDate),
            transactions.Max(x => x.ExpenseDate),
            ResolveAppliedCategory(categories, categoryFilter),
            filteredTransactions.Count,
            filteredTransactions.Sum(x => x.Amount),
            DetermineCurrencyLabel(filteredTransactions.Select(x => x.Currency)),
            categories,
            filteredTransactions);
    }

    private static List<ExpenseCategorySummary> BuildCategorySummaries(IEnumerable<ExpenseStatementTransaction> transactions)
    {
        return transactions
            .GroupBy(x => NormalizeCategory(x.Category))
            .Select(g =>
            {
                var transactionList = g.ToList();
                var categoryName = ResolveDisplayCategoryName(transactionList);
                return new ExpenseCategorySummary(
                    categoryName,
                    transactionList.Count,
                    transactionList.Sum(x => x.Amount),
                    DetermineCurrencyLabel(transactionList.Select(x => x.Currency)));
            })
            .OrderByDescending(x => x.TotalAmount)
            .ThenByDescending(x => x.TransactionCount)
            .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ExpenseStatementTransaction> ApplyCategoryFilter(
        IReadOnlyList<ExpenseStatementTransaction> transactions,
        string? categoryFilter)
    {
        var normalizedFilter = NormalizeCategory(categoryFilter);
        if (string.IsNullOrEmpty(normalizedFilter))
        {
            return transactions.ToList();
        }

        return transactions
            .Where(x => string.Equals(NormalizeCategory(x.Category), normalizedFilter, StringComparison.Ordinal))
            .ToList();
    }

    private static string? ResolveAppliedCategory(
        IReadOnlyList<ExpenseCategorySummary> categories,
        string? categoryFilter)
    {
        var normalizedFilter = NormalizeCategory(categoryFilter);
        if (string.IsNullOrEmpty(normalizedFilter))
        {
            return null;
        }

        return categories
            .FirstOrDefault(x => string.Equals(NormalizeCategory(x.Category), normalizedFilter, StringComparison.Ordinal))
            ?.Category;
    }

    private static string DetermineCurrencyLabel(IEnumerable<string> currencies)
    {
        var distinctCurrencies = currencies
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return distinctCurrencies.Count switch
        {
            0 => "TRY",
            1 => distinctCurrencies[0],
            _ => "Mixed"
        };
    }

    private static string NormalizeCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    private static string ResolveDisplayCategoryName(IEnumerable<ExpenseStatementTransaction> transactions)
    {
        return transactions
            .Select(x => x.Category?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "Other";
    }
}
