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
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public class ExpenseAnalysisService(
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext dbContext,
    IOptions<AiOptions> aiOptions,
    IOptions<CodeSandboxMcpOptions> codeSandboxOptions,
    ILoggerFactory loggerFactory,
    ILogger<ExpenseAnalysisService> logger
) : IExpenseAnalysisService
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AiOptions _aiOptions = aiOptions.Value;
    private readonly CodeSandboxMcpOptions _codeSandboxOptions = codeSandboxOptions.Value;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

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

            var parsedStatement = await ParseStatementWithMcpAsync(pdfText, cancellationToken);
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

    private async Task<ParsedExpenseStatement> ParseStatementWithMcpAsync(string markdown, CancellationToken cancellationToken)
    {
        var pythonCode = await GenerateExtractionPythonAsync(markdown, cancellationToken);
        var toolOutput = await ExecutePythonInSandboxAsync(pythonCode, cancellationToken);

        if (!TryParseExtractionResult(toolOutput, out var extractionResult, out var validationError))
        {
            logger.LogWarning(
                "Generated extraction output was invalid. Retrying once. Error: {Error}",
                validationError);

            pythonCode = await RepairExtractionPythonAsync(markdown, pythonCode, toolOutput, validationError!, cancellationToken);
            toolOutput = await ExecutePythonInSandboxAsync(pythonCode, cancellationToken);

            if (!TryParseExtractionResult(toolOutput, out extractionResult, out validationError))
            {
                throw new InvalidOperationException($"Sandbox extraction returned invalid JSON: {validationError}");
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

    private async Task<string> GenerateExtractionPythonAsync(string markdown, CancellationToken cancellationToken)
    {
        var chatClient = _aiOptions
            .CreateOpenAiClient()
            .GetChatClient(_aiOptions.Model)
            .AsIChatClient();

        var messages = new[]
        {
            new ChatMessage(ChatRole.System, BuildPythonGenerationInstructions()),
            new ChatMessage(ChatRole.User, BuildPythonGenerationInput(markdown))
        };

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions
            {
                Temperature = 0
            },
            cancellationToken);

        return ExtractPythonCode(response.Text);
    }

    private async Task<string> RepairExtractionPythonAsync(
        string markdown,
        string previousPythonCode,
        string toolOutput,
        string validationError,
        CancellationToken cancellationToken)
    {
        var chatClient = _aiOptions
            .CreateOpenAiClient()
            .GetChatClient(_aiOptions.Model)
            .AsIChatClient();

        var messages = new[]
        {
            new ChatMessage(ChatRole.System, BuildPythonRepairInstructions()),
            new ChatMessage(ChatRole.User, BuildPythonRepairInput(markdown, previousPythonCode, toolOutput, validationError))
        };

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions
            {
                Temperature = 0
            },
            cancellationToken);

        return ExtractPythonCode(response.Text);
    }

    private async Task<string> ExecutePythonInSandboxAsync(string pythonCode, CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "code-sandbox-mcp",
            Command = _codeSandboxOptions.Command,
            Arguments = _codeSandboxOptions.Arguments,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CONTAINER_IMAGE"] = _codeSandboxOptions.ContainerImage,
                ["CONTAINER_LANGUAGE"] = _codeSandboxOptions.ContainerLanguage
            }
        });

        await using var mcpClient = await McpClient.CreateAsync(
            transport,
            loggerFactory: _loggerFactory,
            cancellationToken: cancellationToken);

        var result = await mcpClient.CallToolAsync(
            "run_python_code",
            new Dictionary<string, object?>
            {
                ["code"] = pythonCode
            },
            cancellationToken: cancellationToken);

        return ExtractToolText(result);
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
        string toolOutput,
        out SandboxExpenseExtractionResult? result,
        out string? error)
    {
        result = null;
        error = null;

        var jsonPayload = ExtractJsonObject(toolOutput);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            error = "Sandbox output did not contain a JSON object.";
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<SandboxExpenseExtractionResult>(jsonPayload, JsonOptions);
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

            var dedupeKey = $"{item.Date}|{item.Description.Trim()}|{item.Amount.ToString("0.00", CultureInfo.InvariantCulture)}|{itemCurrency}";
            seenRows.Add(dedupeKey);
        }

        var normalizedExpenses = result.Expenses
            .Select(item => item with
            {
                Currency = NormalizeCurrency(item.Currency) ?? normalizedCurrency,
                Description = Regex.Replace(item.Description!.Trim(), @"\s+", " ")
            })
            .ToList();

        result = result with
        {
            Currency = normalizedCurrency,
            Expenses = normalizedExpenses
        };

        return true;
    }

    private static ParsedExpenseStatement MapExtractionResult(SandboxExpenseExtractionResult result)
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

    private static string BuildPythonGenerationInstructions()
    {
        return """
               You write Python 3 code that extracts expense transactions from bank or credit card statement markdown.
               Return only Python code. Do not use Markdown fences. Do not add explanations.

               Hard requirements:
               - Use only Python standard library.
               - Read the provided markdown from a triple-quoted string variable named STATEMENT_MARKDOWN.
               - Print exactly one JSON object to stdout and nothing else.
               - The JSON must match this shape:
                 {
                   "currency": "TRY",
                   "expenses": [
                     {
                       "date": "YYYY-MM-DD",
                       "description": "merchant or transaction description",
                       "amount": 123.45,
                       "currency": "TRY"
                     }
                   ]
                 }
               - Include only actual expense transactions. Exclude payments, refunds, carry-over balances, rewards, statement totals, card limits, and summary rows.
               - Amount must always be positive numbers.
               - Preserve the merchant/description as it appears, but normalize repeated whitespace.
               - If the statement does not explicitly specify item currency, use the statement currency.
               - Before printing the final JSON, self-check whether the sum of extracted expense amounts matches any total spending / total purchases / statement total figures visible in the markdown. If it does not match, revise the extraction first and only then print the final JSON.
               - Prefer being conservative over guessing. Never invent transactions.
               """;
    }

    private static string BuildPythonGenerationInput(string markdown)
    {
        return $$"""
                 Write a deterministic Python extractor for the following statement markdown.

                 STATEMENT_MARKDOWN = r'''
                 {{markdown}}
                 '''
                 """;
    }

    private static string BuildPythonRepairInstructions()
    {
        return """
               You are fixing a Python expense extractor.
               Return only corrected Python code. Do not use Markdown fences. Do not add explanations.
               The corrected script must still print exactly one JSON object to stdout and nothing else.
               Use only Python standard library.
               """;
    }

    private static string BuildPythonRepairInput(
        string markdown,
        string previousPythonCode,
        string toolOutput,
        string validationError)
    {
        return $$"""
                 The previous script failed validation.

                 Validation error:
                 {{validationError}}

                 Previous Python code:
                 {{previousPythonCode}}

                 Sandbox output:
                 {{toolOutput}}

                 Statement markdown:
                 STATEMENT_MARKDOWN = r'''
                 {{markdown}}
                 '''
                 """;
    }

    private static string ExtractPythonCode(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException("AI code generation returned empty content.");
        }

        var trimmed = responseText.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var code = Regex.Replace(trimmed, @"^```(?:python)?\s*", string.Empty, RegexOptions.IgnoreCase);
        code = Regex.Replace(code, @"\s*```$", string.Empty);
        return code.Trim();
    }

    private static string ExtractToolText(CallToolResult result)
    {
        var text = string.Join(
            "\n",
            result.Content
                .OfType<TextContentBlock>()
                .Select(block => block.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));

        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        if (result.StructuredContent is not JsonElement structuredContent)
        {
            return string.Empty;
        }

        return structuredContent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : structuredContent.GetRawText();
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
}

public sealed record MarkitdownConvertResponse(string? Filename, string? Markdown);

public sealed record SandboxExpenseExtractionItem(
    string? Date,
    string? Description,
    decimal Amount,
    string? Currency
);

public sealed record SandboxExpenseExtractionResult(
    string? Currency,
    List<SandboxExpenseExtractionItem>? Expenses
);
