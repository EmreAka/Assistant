using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public record StatementExpenseItem(
    DateOnly Date,
    string Name,
    decimal Price
);

public record ParsedExpenseStatement(
    IReadOnlyList<StatementExpenseItem> Expenses,
    decimal Total
);

public record ExpenseAnalysisResponse(
    bool IsSuccess,
    string UserMessage,
    List<ExpenseModel>? Expenses = null,
    ParsedExpenseStatement? ParsedStatement = null
);

public interface IExpenseAnalysisService
{
    /// <summary>
    /// PDF ekstre dosyasını analiz eder ve harcamaları döndürür.
    /// </summary>
    Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken);
}
