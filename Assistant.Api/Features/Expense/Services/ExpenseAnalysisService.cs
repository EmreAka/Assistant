using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public class ExpenseAnalysisService(
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext dbContext,
    IOptions<AiOptions> aiOptions,
    ILogger<ExpenseAnalysisService> logger
) : IExpenseAnalysisService
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AiOptions _aiOptions = aiOptions.Value;

    public async Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken)
    {
        _ = chatId;

        try
        {
            var pdfText = await ExtractTextFromPdfAsync(pdfStream, cancellationToken);
            if (string.IsNullOrWhiteSpace(pdfText))
            {
                logger.LogWarning("PDF text is empty.");
                return new ExpenseAnalysisResponse(false, "PDF metni okunamadı.");
            }

            var parsedStatement = await ParseStatementWithLlmAsync(pdfText, cancellationToken);
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

    private async Task<ParsedExpenseStatement> ParseStatementWithLlmAsync(string markdown, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Starting LLM expense extraction. MarkdownLength={MarkdownLength} MarkdownPreview={MarkdownPreview}",
            markdown.Length,
            TruncateForLog(markdown));

        var draftJson = await GenerateDraftExtractionAsync(markdown, cancellationToken);
        var reviewedJson = await ReviewDraftExtractionAsync(markdown, draftJson, cancellationToken);

        if (!TryParseExtractionResult(reviewedJson, out var extractionResult, out var validationError))
        {
            logger.LogWarning(
                "Reviewed extraction output was invalid. Retrying once. Error: {Error}",
                validationError);

            reviewedJson = await RepairExtractionAsync(markdown, draftJson, reviewedJson, validationError!, cancellationToken);

            if (!TryParseExtractionResult(reviewedJson, out extractionResult, out validationError))
            {
                throw new InvalidOperationException($"LLM extraction returned invalid JSON: {validationError}");
            }
        }

        var expectedTotal = extractionResult!.StatementTotal;
        if (expectedTotal.HasValue)
        {
            var reviewedTotal = decimal.Round(extractionResult!.Expenses!.Sum(x => x.Amount), 2, MidpointRounding.AwayFromZero);
            var expectedRounded = decimal.Round(expectedTotal.Value, 2, MidpointRounding.AwayFromZero);

            if (reviewedTotal != expectedRounded)
            {
                logger.LogWarning(
                    "Expense total mismatch after review. ExpectedTotal={ExpectedTotal} ReviewedTotal={ReviewedTotal}. Running one reconciliation pass.",
                    expectedRounded,
                    reviewedTotal);

                reviewedJson = await ReconcileTotalMismatchAsync(
                    markdown,
                    draftJson,
                    reviewedJson,
                    expectedRounded,
                    reviewedTotal,
                    cancellationToken);

                if (!TryParseExtractionResult(reviewedJson, out extractionResult, out validationError))
                {
                    throw new InvalidOperationException($"LLM reconciliation returned invalid JSON: {validationError}");
                }

                var reconciledTotal = decimal.Round(extractionResult!.Expenses!.Sum(x => x.Amount), 2, MidpointRounding.AwayFromZero);
                if (reconciledTotal != expectedRounded)
                {
                    throw new InvalidOperationException(
                        $"LLM extraction total mismatch. Expected {expectedRounded:0.00} but got {reconciledTotal:0.00}.");
                }
            }
        }

        return MapExtractionResult(extractionResult!);
    }

    private async Task<string?> ExtractTextFromPdfAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        try
        {
            if (pdfStream.CanSeek)
            {
                pdfStream.Position = 0;
            }

            using var multipartContent = new MultipartFormDataContent();
            using var fileContent = new StreamContent(pdfStream);
            multipartContent.Add(fileContent, "file", "statement.pdf");

            using var httpClient = httpClientFactory.CreateClient(BotServiceRegistration.MarkitdownHttpClientName);
            using var response = await httpClient.PostAsync("/convert", multipartContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Markitdown request failed with status code {StatusCode}.", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<MarkitdownConvertResponse>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(payload?.Markdown))
            {
                logger.LogWarning("Markitdown response did not contain markdown content.");
                return null;
            }

            return payload.Markdown;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting text from PDF via Markitdown.");
            return null;
        }
    }

    private async Task<string> GenerateDraftExtractionAsync(string markdown, CancellationToken cancellationToken)
    {
        var responseText = await RunChatPromptAsync(
            BuildDraftExtractionInstructions(),
            BuildDraftExtractionInput(markdown),
            cancellationToken);

        var draftJson = ExtractJsonObject(responseText) ?? responseText.Trim();
        logger.LogInformation(
            "Generated draft expense extraction. ResponsePreview={ResponsePreview} DraftPreview={DraftPreview}",
            TruncateForLog(responseText),
            TruncateForLog(draftJson));

        return draftJson;
    }

    private async Task<string> ReviewDraftExtractionAsync(string markdown, string draftJson, CancellationToken cancellationToken)
    {
        var responseText = await RunChatPromptAsync(
            BuildReviewInstructions(),
            BuildReviewInput(markdown, draftJson),
            cancellationToken);

        var reviewedJson = ExtractJsonObject(responseText) ?? responseText.Trim();
        logger.LogInformation(
            "Reviewed draft expense extraction. ResponsePreview={ResponsePreview} ReviewedPreview={ReviewedPreview}",
            TruncateForLog(responseText),
            TruncateForLog(reviewedJson));

        return reviewedJson;
    }

    private async Task<string> RepairExtractionAsync(
        string markdown,
        string draftJson,
        string reviewedJson,
        string validationError,
        CancellationToken cancellationToken)
    {
        var responseText = await RunChatPromptAsync(
            BuildRepairInstructions(),
            BuildRepairInput(markdown, draftJson, reviewedJson, validationError),
            cancellationToken);

        var repairedJson = ExtractJsonObject(responseText) ?? responseText.Trim();
        logger.LogInformation(
            "Repaired expense extraction. ValidationError={ValidationError} ResponsePreview={ResponsePreview} RepairedPreview={RepairedPreview}",
            validationError,
            TruncateForLog(responseText),
            TruncateForLog(repairedJson));

        return repairedJson;
    }

    private async Task<string> ReconcileTotalMismatchAsync(
        string markdown,
        string draftJson,
        string reviewedJson,
        decimal expectedTotal,
        decimal actualTotal,
        CancellationToken cancellationToken)
    {
        var responseText = await RunChatPromptAsync(
            BuildTotalReconciliationInstructions(),
            BuildTotalReconciliationInput(markdown, draftJson, reviewedJson, expectedTotal, actualTotal),
            cancellationToken);

        var reconciledJson = ExtractJsonObject(responseText) ?? responseText.Trim();
        logger.LogInformation(
            "Reconciled expense extraction total mismatch. ExpectedTotal={ExpectedTotal} ActualTotal={ActualTotal} ResponsePreview={ResponsePreview} ReconciledPreview={ReconciledPreview}",
            expectedTotal,
            actualTotal,
            TruncateForLog(responseText),
            TruncateForLog(reconciledJson));

        return reconciledJson;
    }

    private async Task<string> RunChatPromptAsync(string systemInstructions, string userInput, CancellationToken cancellationToken)
    {
        var chatClient = _aiOptions
            .CreateOpenAiClient()
            .GetChatClient(_aiOptions.Model)
            .AsIChatClient();

        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, systemInstructions),
                new ChatMessage(ChatRole.User, userInput)
            ],
            new ChatOptions
            {
                Temperature = 0,
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.ExtraHigh
                }
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            throw new InvalidOperationException("AI extraction returned empty content.");
        }

        return response.Text.Trim();
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

    private static bool TryParseExtractionResult(
        string rawJson,
        out ExpenseExtractionResult? result,
        out string? error)
    {
        result = null;
        error = null;

        var jsonPayload = ExtractJsonObject(rawJson);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            error = "Response did not contain a JSON object.";
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<ExpenseExtractionResult>(jsonPayload, JsonOptions);
        }
        catch (Exception ex)
        {
            error = $"JSON parse failed: {ex.Message}";
            return false;
        }

        if (result is null)
        {
            error = "JSON payload could not be deserialized.";
            return false;
        }

        if (result.Expenses is null || result.Expenses.Count == 0)
        {
            error = "No expenses were returned.";
            return false;
        }

        var normalizedCurrency = NormalizeCurrency(result.Currency);
        if (string.IsNullOrWhiteSpace(normalizedCurrency))
        {
            error = "Statement currency is missing.";
            return false;
        }

        if (result.StatementTotal.HasValue && result.StatementTotal.Value <= 0)
        {
            error = "Statement total must be positive when provided.";
            return false;
        }

        var normalizedExpenses = new List<ExpenseExtractionItem>(result.Expenses.Count);
        var seenRows = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in result.Expenses)
        {
            if (item is null)
            {
                error = "Expense item is null.";
                return false;
            }

            if (!DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                error = $"Expense date is invalid: {item.Date}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Description))
            {
                error = "Expense description is missing.";
                return false;
            }

            if (item.Amount <= 0)
            {
                error = $"Expense amount must be positive for '{item.Description}'.";
                return false;
            }

            var itemCurrency = NormalizeCurrency(item.Currency) ?? normalizedCurrency;
            if (!string.Equals(itemCurrency, normalizedCurrency, StringComparison.Ordinal))
            {
                error = $"Mixed currencies are not supported in the same statement: {itemCurrency} vs {normalizedCurrency}.";
                return false;
            }

            var normalizedDescription = Regex.Replace(item.Description.Trim(), @"\s+", " ");
            var dedupeKey = $"{item.Date}|{normalizedDescription}|{item.Amount.ToString("0.00", CultureInfo.InvariantCulture)}|{itemCurrency}";
            if (!seenRows.Add(dedupeKey))
            {
                continue;
            }

            normalizedExpenses.Add(item with
            {
                Description = normalizedDescription,
                Currency = itemCurrency
            });
        }

        if (normalizedExpenses.Count == 0)
        {
            error = "No expenses remained after validation.";
            return false;
        }

        result = result with
        {
            Currency = normalizedCurrency,
            Expenses = normalizedExpenses
        };

        return true;
    }

    private static ParsedExpenseStatement MapExtractionResult(ExpenseExtractionResult result)
    {
        var statementCurrency = result.Currency ?? throw new InvalidOperationException("Statement currency cannot be null after validation.");
        var extractionItems = result.Expenses ?? throw new InvalidOperationException("Expenses cannot be null after validation.");

        var expenses = extractionItems
            .Select(item => new StatementExpenseItem(
                DateOnly.ParseExact(item.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                item.Description!,
                item.Amount,
                item.Currency ?? statementCurrency))
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Price)
            .ToList();

        return new ParsedExpenseStatement(
            expenses,
            expenses.Sum(x => x.Price),
            statementCurrency);
    }

    private static string BuildDraftExtractionInstructions()
    {
        return """
               Extract expense transactions from statement markdown and return JSON only.

               Output schema:
               {
                 "currency": "TRY",
                 "statement_total": 1234.56,
                 "expenses": [
                   {
                     "date": "YYYY-MM-DD",
                     "description": "merchant or transaction description",
                     "amount": 123.45,
                     "currency": "TRY"
                   }
                 ]
               }

               Rules:
               - Include only real spending transactions.
               - If the statement explicitly shows a billing-period or statement spending/borc total relevant to the extracted expenses, put it in `statement_total`.
               - If no trustworthy total is visible, set `statement_total` to null.
               - Exclude payments, previous balance carry-over, rewards, bonuses, refunds, fees only if clearly not spending, card limits, statement totals, and summary rows.
               - Amounts must be positive.
               - Preserve merchant names, but normalize whitespace.
               - Use the statement currency unless a transaction clearly shows another currency.
               - Prefer completeness over brevity: include every actual spending row you can justify from the markdown.
               - Return JSON only. No markdown fences. No commentary.
               """;
    }

    private static string BuildDraftExtractionInput(string markdown)
    {
        return $$"""
                 Extract all spending transactions from this statement markdown.
                 Think carefully about installment rows, inline rows, and markdown table rows.

                 Markdown:
                 {{markdown}}
                 """;
    }

    private static string BuildReviewInstructions()
    {
        return """
               You are reviewing a draft expense extraction against statement markdown.
               Return corrected JSON only, using the same schema.

               Review checklist:
               - Add any missing spending rows.
               - Remove rows that are not real spending.
               - Fix dates, descriptions, currencies, and amounts.
               - Check installment lines and mixed markdown table / inline formats carefully.
               - Re-evaluate `statement_total`. It must be the explicit total visible in the statement that corresponds to the extracted expenses, or null if not trustworthy.
               - Self-check the extracted total against `statement_total` when available.
               - Do not invent rows that cannot be justified from the markdown.
               - Return JSON only. No markdown fences. No commentary.
               """;
    }

    private static string BuildReviewInput(string markdown, string draftJson)
    {
        return $$"""
                 Statement markdown:
                 {{markdown}}

                 Draft extraction JSON:
                 {{draftJson}}
                 """;
    }

    private static string BuildRepairInstructions()
    {
        return """
               Repair an expense extraction JSON so it becomes valid and complete.
               Return corrected JSON only, using the same schema.

               Requirements:
               - currency must be present
               - statement_total may be null, otherwise it must be a positive number
               - expenses must be a non-empty array
               - each expense must have ISO date, non-empty description, positive amount, and currency
                - remove invalid rows, fix obvious formatting issues, and add clearly missing spending rows when needed
               - return JSON only
               """;
    }

    private static string BuildTotalReconciliationInstructions()
    {
        return """
               You are reconciling an expense extraction JSON against the statement markdown.
               Return corrected JSON only, using the same schema.

               Your job:
               - The statement indicates an expected total spending amount.
               - The current extracted expenses do not match that total.
               - Find the most likely missing, misread, duplicate, or mis-amounted spending rows.
               - Prefer fixing the smallest number of rows needed to make the total match.
               - Do not add rows that cannot be justified from the markdown.
               - Exclude non-spending items such as payments, carry-over balances, rewards, and summary lines.
               - Return JSON only. No markdown fences. No commentary.
               """;
    }

    private static string BuildRepairInput(
        string markdown,
        string draftJson,
        string reviewedJson,
        string validationError)
    {
        return $$"""
                 Validation error:
                 {{validationError}}

                 Statement markdown:
                 {{markdown}}

                 Original draft JSON:
                 {{draftJson}}

                 Reviewed JSON:
                 {{reviewedJson}}
                 """;
    }

    private static string BuildTotalReconciliationInput(
        string markdown,
        string draftJson,
        string reviewedJson,
        decimal expectedTotal,
        decimal actualTotal)
    {
        return $$"""
                 Expected total spending from statement JSON: {{expectedTotal.ToString("0.00", CultureInfo.InvariantCulture)}}
                 Current extracted total: {{actualTotal.ToString("0.00", CultureInfo.InvariantCulture)}}
                 Difference: {{(expectedTotal - actualTotal).ToString("0.00", CultureInfo.InvariantCulture)}}

                 Statement markdown:
                 {{markdown}}

                 Original draft JSON:
                 {{draftJson}}

                 Current reviewed JSON:
                 {{reviewedJson}}
                 """;
    }

    private static string? ExtractJsonObject(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var start = rawText.IndexOf('{', StringComparison.Ordinal);
        var end = rawText.LastIndexOf('}');

        if (start < 0 || end < start)
        {
            return null;
        }

        return rawText[start..(end + 1)];
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            "TL" => "TRY",
            "₺" => "TRY",
            _ => normalized
        };
    }

    private static string NormalizeForFingerprint(string value)
    {
        return Regex.Replace(value.ToUpperInvariant(), @"\s+", " ").Trim();
    }

    private static DateTime ToUtcDate(DateOnly date)
    {
        return DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    }

    private static string TruncateForLog(string? value, int maxLength = 4000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "\n...[truncated]";
    }
}

public sealed record MarkitdownConvertResponse(string? Filename, string? Markdown);

public sealed record ExpenseExtractionItem(
    string? Date,
    string? Description,
    decimal Amount,
    string? Currency
);

public sealed record ExpenseExtractionResult(
    string? Currency,
    decimal? StatementTotal,
    List<ExpenseExtractionItem>? Expenses
);
