using System.ComponentModel;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Domain.Entities;
using Assistant.Api.Extensions;
using Assistant.Api.Services.Abstracts;
using Google.GenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Services.Concretes;

public class ExpenseAnalysisService(
    IPersonalityService personalityService,
    IOptions<AiOptions> aiOptions,
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext dbContext,
    ILogger<ExpenseAnalysisService> logger,
    ILogger<ExpenseToolFunctions> toolLogger
) : IExpenseAnalysisService
{
    private readonly AiOptions _aiOptions = aiOptions.Value;

    public async Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken)
    {
        try
        {
            var pdfText = await ExtractTextFromPdfAsync(pdfStream, cancellationToken);
            if (string.IsNullOrWhiteSpace(pdfText))
            {
                logger.LogWarning("PDF text is empty.");
                return new ExpenseAnalysisResponse(false, "PDF metni okunamadı.");
            }

            var expenseToolFunctions = new ExpenseToolFunctions(userId, dbContext, toolLogger);
            var tools = new List<AITool> { AIFunctionFactory.Create(expenseToolFunctions.RegisterExpenses) };

            var geminiClient = new Client(apiKey: _aiOptions.GoogleApiKey);
            var chatClient = geminiClient
                .AsIChatClient(_aiOptions.Model)
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();

            var agent = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Reasoning = new ReasoningOptions() { Effort = ReasoningEffort.Low },
                        Temperature = 1,
                        Tools = tools,
                        ModelId = _aiOptions.Model,
                    },
                    AIContextProviders = [new PersonalityContextProvider(chatId, personalityService)]
                }
            );

            List<ChatMessage> thread =
            [
                new(ChatRole.System, BuildExpensePrompt()),
                new(ChatRole.User, $"Analyze this statement, create a single billing period expense summary, and register it:\n\n{pdfText}")
            ];

            var response = await agent.RunAsync(thread, cancellationToken: cancellationToken);
            var responseText = response.Text?.Trim() ?? string.Empty;

            if (expenseToolFunctions.CapturedExpenses.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    return new ExpenseAnalysisResponse(true, responseText, expenseToolFunctions.CapturedExpenses);
                }

                return new ExpenseAnalysisResponse(true, "Ekstre dönem özeti kaydedildi.", expenseToolFunctions.CapturedExpenses);
            }

            if (!string.IsNullOrWhiteSpace(responseText))
            {
                return new ExpenseAnalysisResponse(false, responseText);
            }

            return new ExpenseAnalysisResponse(false, "Ekstre analiz edilemedi.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error analyzing statement with Agent.");
            return new ExpenseAnalysisResponse(false, "Ekstre analizi sırasında bir hata oluştu.");
        }
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
            //fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
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

    private static string BuildExpensePrompt()
    {
        return """
               You are an expense analysis assistant. 
               Analyze the provided credit card statement text and extract a single expense summary for the full billing period.
               Call the RegisterExpenses tool exactly once with the billing period total.
               
               Rules:
               - The amount must be the total spending for the billing period, not individual transactions.
               - Description must be a short natural-language summary of the statement period, not raw OCR text.
               - Extract billing period start date and billing period end date from the statement.
               - Ensure amount, currency, description, billing period start date, and billing period end date are accurate.
               - After calling the tool, briefly summarize the saved billing period expense to the user in your own personality.
               """;
    }
}

public sealed record MarkitdownConvertResponse(string? Filename, string? Markdown);

public class ExpenseToolFunctions(int userId, ApplicationDbContext dbContext, ILogger logger)
{
    public List<Expense> CapturedExpenses { get; } = [];

    [Description("Registers a single billing period expense summary into the system.")]
    public async Task RegisterExpenses(
        [Description("The billing period expense summary to register for this statement.")] ExpenseInput expense)
    {
        logger.LogInformation("Capturing billing period expense summary for user {UserId}", userId);

        var expenseEntity = new Expense
        {
            TelegramUserId = userId,
            Amount = expense.Amount,
            Currency = expense.Currency ?? "TRY",
            Description = expense.Description ?? "Ekstre dönem özeti",
            BillingPeriodStartDate = expense.BillingPeriodStartDate,
            BillingPeriodEndDate = expense.BillingPeriodEndDate,
            CreatedAt = DateTime.UtcNow
        };

        await dbContext.Expenses.AddAsync(expenseEntity);
        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Captured Expense Summary - Amount: {Amount}, Currency: {Currency}, Description: {Description}, BillingPeriodStartDate: {BillingPeriodStartDate}, BillingPeriodEndDate: {BillingPeriodEndDate}",
            expenseEntity.Amount,
            expenseEntity.Currency,
            expenseEntity.Description,
            expenseEntity.BillingPeriodStartDate,
            expenseEntity.BillingPeriodEndDate);

        CapturedExpenses.Add(expenseEntity);
    }
}

public record ExpenseInput(
    [Description("The amount of the expense (e.g., 150.50).")] decimal Amount,
    [Description("The currency of the expense (e.g., TRY, USD). Defaults to TRY.")] string? Currency,
    [Description("A short natural-language summary of the billing period spending.")] string? Description,
    [Description("The start date of the credit card billing period covered by this statement.")] DateTime BillingPeriodStartDate,
    [Description("The end date of the credit card billing period covered by this statement.")] DateTime BillingPeriodEndDate
);
