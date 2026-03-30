using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public class ExpenseAnalysisService(
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext dbContext,
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

            var parsedStatement = await ExtractFromPdfWithOpenRouterAsync(pdfBytes, cancellationToken);
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

    private async Task<ParsedExpenseStatement?> ExtractFromPdfWithOpenRouterAsync(byte[] pdfBytes, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Starting primary expense extraction via OpenRouter PDF. PdfSizeBytes={PdfSizeBytes}",
                pdfBytes.Length);

            var extractionJson = await RequestStructuredExpenseExtractionFromPdfAsync(pdfBytes, cancellationToken);
            if (!TryParseExtractionResult(extractionJson, out var extractionResult, out var validationError))
            {
                logger.LogWarning(
                    "OpenRouter PDF extraction returned invalid JSON. Error={Error} ResponsePreview={ResponsePreview}",
                    validationError,
                    TruncateForLog(extractionJson));
                return null;
            }

            if (extractionResult!.StatementTotal.HasValue)
            {
                var statementTotal = decimal.Round(extractionResult.StatementTotal.Value, 2, MidpointRounding.AwayFromZero);
                var extractedTotal = decimal.Round(extractionResult.Expenses!.Sum(x => x.Amount), 2, MidpointRounding.AwayFromZero);

                if (statementTotal != extractedTotal)
                {
                    logger.LogWarning(
                        "OpenRouter PDF extraction total mismatch. StatementTotal={StatementTotal} ExtractedTotal={ExtractedTotal}.",
                        statementTotal,
                        extractedTotal);
                    return null;
                }
            }

            return MapExtractionResult(extractionResult);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenRouter PDF extraction failed.");
            return null;
        }
    }

    private async Task<string> RequestStructuredExpenseExtractionFromPdfAsync(byte[] pdfBytes, CancellationToken cancellationToken)
    {
        var pdfDataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(pdfBytes)}";

        using var client = httpClientFactory.CreateClient(BotServiceRegistration.OpenRouterHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = _aiOptions.OpenRouter.Model,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = BuildDraftExtractionInstructions()
                    },
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = BuildOpenRouterPdfUserPrompt()
                            },
                            new
                            {
                                type = "file",
                                file = new
                                {
                                    filename = "statement.pdf",
                                    file_data = pdfDataUrl
                                }
                            }
                        }
                    }
                },
                plugins = new object[]
                {
                    new
                    {
                        id = "file-parser",
                        pdf = new
                        {
                            engine = "native"
                        }
                    }
                },
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "expense_statement_extraction",
                        strict = true,
                        schema = BuildExpenseExtractionSchema()
                    }
                },
                temperature = 0
            })
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"OpenRouter PDF request failed with status {(int)response.StatusCode}: {TruncateForLog(errorBody)}");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var jsonDocument = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var messageContent = ExtractOpenRouterMessageContent(jsonDocument.RootElement)?.Trim();

        if (string.IsNullOrWhiteSpace(messageContent))
        {
            throw new InvalidOperationException("OpenRouter PDF request returned empty content.");
        }

        logger.LogInformation(
            "OpenRouter PDF extraction completed. ResponsePreview={ResponsePreview}",
            TruncateForLog(messageContent));

        return messageContent;
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
                item.Currency ?? statementCurrency,
                item.Category ?? "Other"))
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
                     "currency": "TRY",
                     "category": "Food & Dining"
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
               - Do not collapse repeated rows just because date, merchant, and amount are the same. If the statement lists the same spending row multiple times, keep each occurrence as a separate expense.
               - Prefer completeness over brevity: include every actual spending row you can justify from the markdown.
               - Assign each expense a category from this fixed list: "Food & Dining", "Transportation", "Shopping", "Entertainment", "Travel", "Health & Pharmacy", "Subscriptions & Software", "Utilities & Bills", "Education", "Other".
               - "Subscriptions & Software" covers streaming services, SaaS, AI tools (ChatGPT, Claude, Gemini, Midjourney, etc.), app stores, and cloud services.
               - Use "Other" only when none of the above clearly fits.
               - Return JSON only. No markdown fences. No commentary.
               """;
    }

    private static string BuildOpenRouterPdfUserPrompt()
    {
        return """
               Extract all spending transactions from this statement.
               Think carefully about installment rows, inline rows, and markdown table rows.
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

    private static object BuildExpenseExtractionSchema()
    {
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                currency = new
                {
                    type = "string",
                    description = "Statement currency code, for example TRY."
                },
                statement_total = new
                {
                    type = new[] { "number", "null" },
                    description = "The explicit statement total relevant to the extracted expenses, or null if not trustworthy."
                },
                expenses = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            date = new
                            {
                                type = "string",
                                description = "Expense date in ISO format yyyy-MM-dd."
                            },
                            description = new
                            {
                                type = "string",
                                description = "Merchant or transaction description."
                            },
                            amount = new
                            {
                                type = "number",
                                description = "Positive expense amount."
                            },
                            currency = new
                            {
                                type = "string",
                                description = "Expense currency code."
                            },
                            category = new
                            {
                                type = "string",
                                description = "Expense category from the fixed list: Food & Dining, Transportation, Shopping, Entertainment, Travel, Health & Pharmacy, Subscriptions & Software, Utilities & Bills, Education, Other."
                            }
                        },
                        required = new[] { "date", "description", "amount", "currency", "category" }
                    }
                }
            },
            required = new[] { "currency", "statement_total", "expenses" }
        };
    }

    private static string? ExtractOpenRouterMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join(
                "\n",
                content.EnumerateArray()
                    .Select(ExtractContentPartText)
                    .Where(static text => !string.IsNullOrWhiteSpace(text))),
            _ => null
        };
    }

    private static string? ExtractContentPartText(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (part.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
        {
            return directText.GetString();
        }

        if (part.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
            && part.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        return null;
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

public sealed record ExpenseExtractionItem(
    string? Date,
    string? Description,
    decimal Amount,
    string? Currency,
    string? Category
);

public sealed record ExpenseExtractionResult(
    string? Currency,
    decimal? StatementTotal,
    List<ExpenseExtractionItem>? Expenses
);
