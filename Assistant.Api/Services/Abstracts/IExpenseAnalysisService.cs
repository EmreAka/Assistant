using Assistant.Api.Domain.Entities;

namespace Assistant.Api.Services.Abstracts;

public record ExpenseAnalysisResponse(
    bool IsSuccess,
    string UserMessage,
    List<Expense>? Expenses = null
);

public interface IExpenseAnalysisService
{
    /// <summary>
    /// PDF ekstre dosyasını analiz eder ve harcamaları döndürür.
    /// </summary>
    Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken);
}
