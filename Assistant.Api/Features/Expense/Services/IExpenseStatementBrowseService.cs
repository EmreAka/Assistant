namespace Assistant.Api.Features.Expense.Services;

public interface IExpenseStatementBrowseService
{
    Task<ExpenseStatementOverview?> GetOverviewAsync(int userId, CancellationToken cancellationToken);

    Task<ExpenseStatementDetail?> GetStatementDetailAsync(
        int userId,
        string statementFingerprint,
        string? categoryFilter,
        CancellationToken cancellationToken);
}

public sealed record ExpenseStatementOverview(
    decimal TotalAmount,
    int TransactionCount,
    decimal Last30DaysTotal,
    DateTime LastExpenseDate,
    IReadOnlyList<ExpenseStatementPeriod> StatementPeriods,
    IReadOnlyList<ExpenseCategorySummary> Categories);

public sealed record ExpenseStatementPeriod(
    string Fingerprint,
    DateTime StartDate,
    DateTime EndDate,
    int TransactionCount,
    decimal TotalAmount,
    string Currency);

public sealed record ExpenseStatementDetail(
    string Fingerprint,
    DateTime StartDate,
    DateTime EndDate,
    string? AppliedCategory,
    int TransactionCount,
    decimal TotalAmount,
    string Currency,
    IReadOnlyList<ExpenseCategorySummary> Categories,
    IReadOnlyList<ExpenseStatementTransaction> Transactions);

public sealed record ExpenseCategorySummary(
    string Category,
    int TransactionCount,
    decimal TotalAmount,
    string Currency);

public sealed record ExpenseStatementTransaction(
    DateTime ExpenseDate,
    decimal Amount,
    string Currency,
    string Description,
    string Category);
