using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public class ExpenseAnalysisService(
    ApplicationDbContext dbContext,
    IMarkdownConverter markdownConverter,
    IOptions<AiProvidersOptions> aiOptions,
    ILogger<ExpenseAnalysisService> logger
) : IExpenseAnalysisService
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AiProvidersOptions _aiOptions = aiOptions.Value;

    public async Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken)
    {
        _ = chatId;

        try
        {
            var pdfBytes = await ReadPdfBytesAsync(pdfStream, cancellationToken);
            if (pdfBytes.Length == 0)
            {
                logger.LogWarning("PDF stream is empty.");
                return new ExpenseAnalysisResponse(false, "PDF dosyası okunamadı.");
            }

            using var pdfMemoryStream = new MemoryStream(pdfBytes, writable: false);
            var statementMarkdown = await markdownConverter.ConvertToMarkdownAsync(pdfMemoryStream, cancellationToken);
            if (string.IsNullOrWhiteSpace(statementMarkdown))
            {
                logger.LogWarning("DataLab returned empty markdown for statement PDF.");
                return new ExpenseAnalysisResponse(
                    false,
                    "Ekstre şu anda işlenemedi. Lütfen biraz sonra tekrar deneyin.");
            }

            var parsedStatement = await ExtractFromMarkdownWithGeminiAgentAsync(statementMarkdown, cancellationToken);
            if (parsedStatement is null)
            {
                return new ExpenseAnalysisResponse(
                    false,
                    "Ekstre şu anda işlenemedi. Lütfen biraz sonra tekrar deneyin.");
            }

            if (parsedStatement.Expenses.Count == 0)
            {
                logger.LogWarning("No expenses were parsed from the statement.");
                return new ExpenseAnalysisResponse(false, "Ekstrede işlenebilir harcama bulunamadı.");
            }

            var fingerprint = ComputeStatementFingerprint(parsedStatement);
            var existingExpenses = await dbContext.Expenses
                .Where(x => x.TelegramUserId == userId && x.StatementFingerprint == fingerprint)
                .OrderBy(x => x.ExpenseDate)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            if (existingExpenses.Count > 0)
            {
                return new ExpenseAnalysisResponse(
                    true,
                    BuildDuplicateMessage(parsedStatement),
                    existingExpenses,
                    parsedStatement);
            }

            var savedExpenses = await SaveParsedExpensesAsync(userId, parsedStatement, fingerprint, cancellationToken);

            return new ExpenseAnalysisResponse(
                true,
                BuildUserMessage(parsedStatement),
                savedExpenses,
                parsedStatement);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error analyzing statement.");
            return new ExpenseAnalysisResponse(false, "Ekstre analizi sırasında bir hata oluştu.");
        }
    }

    private async Task<byte[]> ReadPdfBytesAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        if (pdfStream.CanSeek)
        {
            pdfStream.Position = 0;
        }

        using var memoryStream = new MemoryStream();
        await pdfStream.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
    }

    private async Task<ParsedExpenseStatement?> ExtractFromMarkdownWithGeminiAgentAsync(string statementMarkdown, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Starting primary expense extraction via Gemini agent markdown. MarkdownLength={MarkdownLength}",
                statementMarkdown.Length);

            var result = await RequestStructuredExpenseExtractionFromMarkdownAsync(statementMarkdown, cancellationToken);
            var expenses = RemoveOffsetTransactions(result.Transactions)
                .Where(item => IsOutgoing(item.Direction))
                .Select(item => new StatementExpenseItem(
                    item.Date,
                    item.Description,
                    item.Amount,
                    item.Currency,
                    item.Category))
                .OrderBy(item => item.Date)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Price)
                .ToList();

            return new ParsedExpenseStatement(
                expenses,
                expenses.Sum(x => x.Price),
                result.Currency);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gemini agent markdown extraction failed.");
            return null;
        }
    }

    private async Task<ExpenseExtractionResult> RequestStructuredExpenseExtractionFromMarkdownAsync(string statementMarkdown, CancellationToken cancellationToken)
    {
        var options = _aiOptions.GoogleAIStudio;
        var responseFormat = BuildExpenseExtractionResponseFormat();
        using var chatClient = options.CreateGoogleGenAIChatClient();
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = BuildDraftExtractionInstructions(),
                    Temperature = 0
                }
            });

        var session = await agent.CreateSessionAsync(cancellationToken);
        var response = await agent.RunAsync<ExpenseExtractionResult>(
            BuildMarkdownUserPrompt(statementMarkdown),
            session,
            JsonOptions,
            new AgentRunOptions
            {
                ResponseFormat = responseFormat
            },
            cancellationToken: cancellationToken);

        logger.LogInformation(
            "Gemini agent markdown extraction completed. TransactionCount={TransactionCount}",
            response.Result.Transactions.Count);

        return response.Result;
    }

    private static ChatResponseFormatJson BuildExpenseExtractionResponseFormat()
    {
        return ChatResponseFormat.ForJsonSchema<ExpenseExtractionResult>(
            JsonOptions,
            schemaName: "expense_extraction",
            schemaDescription: "Structured expense transactions extracted from a statement markdown document.");
    }

    private async Task<List<ExpenseModel>> SaveParsedExpensesAsync(
        int userId,
        ParsedExpenseStatement parsedStatement,
        string statementFingerprint,
        CancellationToken cancellationToken)
    {
        var createdAt = DateTime.UtcNow;
        var expenseEntities = parsedStatement.Expenses
            .Select(expense => new ExpenseModel
            {
                TelegramUserId = userId,
                ExpenseDate = ToUtcDate(expense.Date),
                Amount = expense.Price,
                Currency = expense.Currency,
                Description = expense.Name,
                Category = expense.Category,
                StatementFingerprint = statementFingerprint,
                CreatedAt = createdAt
            })
            .ToList();

        await dbContext.Expenses.AddRangeAsync(expenseEntities, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Saved statement expenses for user {UserId}. Transactions: {TransactionCount}, Total: {Total}, Fingerprint: {StatementFingerprint}",
            userId,
            parsedStatement.Expenses.Count,
            parsedStatement.Total,
            statementFingerprint);

        return expenseEntities;
    }

    private static string BuildUserMessage(ParsedExpenseStatement parsedStatement)
    {
        var start = parsedStatement.Expenses.Min(x => x.Date);
        var end = parsedStatement.Expenses.Max(x => x.Date);

        return
            $"{parsedStatement.Expenses.Count} işlem kaydedildi. " +
            $"Toplam harcama: {parsedStatement.Total.ToString("N2", TurkishCulture)} {parsedStatement.Currency} " +
            $"({start:dd.MM.yyyy} - {end:dd.MM.yyyy}).";
    }

    private static string BuildDuplicateMessage(ParsedExpenseStatement parsedStatement)
    {
        var start = parsedStatement.Expenses.Min(x => x.Date);
        var end = parsedStatement.Expenses.Max(x => x.Date);

        return
            $"Bu ekstre zaten içeri aktarılmış. " +
            $"{parsedStatement.Expenses.Count} işlem, toplam {parsedStatement.Total.ToString("N2", TurkishCulture)} {parsedStatement.Currency} " +
            $"({start:dd.MM.yyyy} - {end:dd.MM.yyyy}).";
    }

    private static string ComputeStatementFingerprint(ParsedExpenseStatement parsedStatement)
    {
        var payload = string.Join(
            "\n",
            parsedStatement.Expenses.Select(expense =>
                $"{expense.Date:yyyy-MM-dd}|{NormalizeForFingerprint(expense.Name)}|{expense.Price.ToString("0.00", CultureInfo.InvariantCulture)}|{expense.Currency}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string BuildDraftExtractionInstructions()
    {
        return """
               Extract statement transactions from statement markdown and return JSON only.

               Output schema:
               {
                 "currency": "TRY",
                 "statement_total": 1234.56,
                 "transactions": [
                   {
                     "date": "YYYY-MM-DD",
                     "description": "merchant or transaction description",
                     "amount": 123.45,
                     "direction": "outgoing",
                     "currency": "TRY",
                     "category": "Food & Dining"
                   }
                 ]
               }

               Rules:
               - Include real merchant transaction rows, both spending and refunds/credits.
               - If the statement explicitly shows a billing-period or statement spending/borc total relevant to the extracted expenses, put it in `statement_total`.
               - If no trustworthy total is visible, set `statement_total` to null.
               - Exclude payments, previous balance carry-over, rewards, bonuses, fees only if clearly not merchant transactions, card limits, statement totals, and summary rows.
               - Set `direction` to "outgoing" for spending rows.
               - Set `direction` to "incoming" for refund/credit/reversal rows.
               - Positive signed amounts such as `+4500`, `+4.500,00`, `4500+`, or `4.500,00+` are `incoming`.
               - Rows with refund words such as iade, iptal, alacak, refund, reversal, chargeback, or credit are `incoming`.
               - Example: `05 Haziran 2026 GARENTA 4.500,00` is `outgoing`, and `10 Haziran 2026 GARENTA 4.500,00+` is `incoming`.
               - Amounts must be positive absolute values without sign.
               - Preserve merchant names, but normalize whitespace.
               - For incoming refund/credit rows, use the same merchant description that would be used for the matching outgoing spending row. Do not include plus signs or refund words in `description` when the merchant can be identified.
               - Use the statement currency unless a transaction clearly shows another currency.
               - Do not collapse repeated rows just because date, merchant, and amount are the same. If the statement lists the same transaction row multiple times, keep each occurrence as a separate transaction.
               - Prefer completeness over brevity: include every actual merchant transaction row you can justify from the markdown.
               - Assign each transaction a category from this fixed list: "Food & Dining", "Transportation", "Shopping", "Entertainment", "Travel", "Health & Pharmacy", "Subscriptions & Software", "Utilities & Bills", "Education", "Other".
               - "Subscriptions & Software" covers streaming services, SaaS, AI tools (ChatGPT, Claude, Gemini, Midjourney, etc.), app stores, and cloud services.
               - Use "Other" only when none of the above clearly fits.
               - Return JSON only. No markdown fences. No commentary.
               """;
    }

    private static string BuildMarkdownUserPrompt(string statementMarkdown)
    {
        return $"""
               Extract all merchant transaction rows from this statement, including spending and refunds/credits.
               Think carefully about installment rows, inline rows, and markdown table rows.

               Statement markdown:
               {statementMarkdown}
               """;
    }

    private static List<ExpenseExtractionItem> RemoveOffsetTransactions(IReadOnlyList<ExpenseExtractionItem> transactions)
    {
        var incomingCounts = transactions
            .Where(item => IsIncoming(item.Direction))
            .GroupBy(BuildOffsetKey)
            .ToDictionary(group => group.Key, group => group.Count());

        var filtered = new List<ExpenseExtractionItem>();

        foreach (var item in transactions.Where(item => !IsIncoming(item.Direction)))
        {
            var key = BuildOffsetKey(item);
            if (incomingCounts.TryGetValue(key, out var count) && count > 0)
            {
                incomingCounts[key] = count - 1;
                continue;
            }

            filtered.Add(item);
        }

        return filtered;
    }

    private static string BuildOffsetKey(ExpenseExtractionItem item)
    {
        var amount = decimal.Round(Math.Abs(item.Amount), 2, MidpointRounding.AwayFromZero);
        return $"{NormalizeForFingerprint(item.Description)}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}|{item.Currency.Trim().ToUpperInvariant()}";
    }

    private static bool IsIncoming(string direction)
    {
        return direction.Equals("incoming", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOutgoing(string direction)
    {
        return direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForFingerprint(string value)
    {
        return Regex.Replace(value.ToUpperInvariant(), @"\s+", " ").Trim();
    }

    private static DateTime ToUtcDate(DateOnly date)
    {
        return DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    }
}

[Description("One merchant transaction extracted from the statement. Include both outgoing spending rows and incoming refund/credit/reversal rows with the correct direction.")]
public sealed record ExpenseExtractionItem(
    [property: Description("Expense date in ISO yyyy-MM-dd format.")]
    DateOnly Date,
    [property: Description("Merchant or transaction description with normalized whitespace.")]
    string Description,
    [property: Description("Positive absolute transaction amount, without sign.")]
    decimal Amount,
    [property: Description("Transaction direction. Use exactly \"outgoing\" for spending and exactly \"incoming\" for refunds, credits, reversals, or values shown with leading/trailing plus signs.")]
    string Direction,
    [property: Description("Expense currency code, for example TRY.")]
    string Currency,
    [property: Description("Expense category from the fixed list: Food & Dining, Transportation, Shopping, Entertainment, Travel, Health & Pharmacy, Subscriptions & Software, Utilities & Bills, Education, Other.")]
    string Category
);

[Description("Structured transaction extraction result for a statement markdown document.")]
public sealed record ExpenseExtractionResult(
    [property: Description("Statement currency code, for example TRY.")]
    string Currency,
    [property: JsonPropertyName("statement_total")]
    [property: Description("Explicit statement spending total relevant to the extracted expenses, or null if not trustworthy.")]
    decimal? StatementTotal,
    [property: JsonPropertyName("transactions")]
    [property: Description("All real merchant transactions, including outgoing spending and incoming refunds/credits/reversals with direction set.")]
    List<ExpenseExtractionItem> Transactions
);
