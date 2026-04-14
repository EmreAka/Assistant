using System.Globalization;
using System.Text;
using Assistant.Api.Data;
using Assistant.Api.Features.Expense.Services;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Features.Expense.Commands;

public class ExpenseCommand(
    IExpenseAnalysisService expenseAnalysisService,
    IExpenseStatementBrowseService expenseStatementBrowseService,
    ApplicationDbContext dbContext,
    ITelegramResponseSender responseSender,
    ILogger<ExpenseCommand> logger
) : IBotCommand
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    public string Command => "expense";
    public string Description => "Harcamalarınızı yönetir ve ekstre analizi yapar.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken
    )
    {
        var message = update.Message;
        if (message?.Chat is null) return;

        // 1. PDF Analizi (Document geldiğinde)
        if (message.Type == MessageType.Document)
        {
            await HandlePdfStatementAsync(message, client, cancellationToken);
            return;
        }

        var user = await dbContext.TelegramUsers
            .FirstOrDefaultAsync(u => u.ChatId == message.Chat.Id, cancellationToken);

        if (user == null)
        {
            await responseSender.SendResponseAsync(
                message.Chat.Id,
                "Önce /start komutu ile kaydolmalısınız.",
                cancellationToken);
            return;
        }

        var userInput = ExtractUserInput(message.Text);
        if (string.IsNullOrWhiteSpace(userInput))
        {
            var summaryMessage = await BuildDeterministicSummaryAsync(user.Id, cancellationToken);
            await responseSender.SendResponseAsync(message.Chat.Id, summaryMessage, cancellationToken);
            return;
        }

        if (TryParseStatementRequest(userInput, out var statementNumber, out var categoryFilter))
        {
            var statementDetailMessage = await BuildStatementDetailMessageAsync(
                user.Id,
                statementNumber,
                categoryFilter,
                cancellationToken);
            await responseSender.SendResponseAsync(message.Chat.Id, statementDetailMessage, cancellationToken);
            return;
        }

        var redirectedSummaryMessage = await BuildDeterministicSummaryAsync(user.Id, cancellationToken, includeChatDirection: true);
        await responseSender.SendResponseAsync(message.Chat.Id, redirectedSummaryMessage, cancellationToken);
    }

    private async Task HandlePdfStatementAsync(Message message, ITelegramBotClient client, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        
        try 
        {
            var user = await dbContext.TelegramUsers
                .FirstOrDefaultAsync(u => u.ChatId == chatId, cancellationToken);
            
            if (user == null)
            {
                await client.SendMessage(chatId, "Önce /start komutu ile kaydolmalısınız.", cancellationToken: cancellationToken);
                return;
            }

            await client.SendMessage(chatId, "📄 Ekstre dosyanı aldım, analiz ediyorum... Lütfen bekleyin.", cancellationToken: cancellationToken);

            using var pdfStream = new MemoryStream();
            await client.GetInfoAndDownloadFile(message.Document!.FileId, pdfStream, cancellationToken);
            pdfStream.Position = 0;

            var result = await expenseAnalysisService.AnalyzeStatementAsync(pdfStream, chatId, user.Id, cancellationToken);

            await client.SendMessage(
                chatId: chatId,
                text: result.UserMessage,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling PDF statement.");
            await client.SendMessage(chatId, "⚠️ Ekstre analizi sırasında bir hata oluştu.", cancellationToken: cancellationToken);
        }
    }

    private async Task<string> BuildDeterministicSummaryAsync(
        int userId,
        CancellationToken cancellationToken,
        bool includeChatDirection = false)
    {
        var overview = await expenseStatementBrowseService.GetOverviewAsync(userId, cancellationToken);
        if (overview is null)
        {
            var emptyMessage = """
                   Henüz kayıtlı harcama yok.

                   İpucu: Kredi kartı ekstreni (PDF) göndererek harcamalarını otomatik ekleyebilirsin.
                   """;

            if (!includeChatDirection)
            {
                return emptyMessage;
            }

            return """
                   Henüz kayıtlı harcama yok.

                   İpucu: Kredi kartı ekstreni (PDF) göndererek harcamalarını otomatik ekleyebilirsin.
                   Harcama sorularını normal sohbette sorabilirsin.
                   """;
        }

        var overallCurrency = DetermineOverallCurrencyLabel(overview.StatementPeriods);
        var builder = new StringBuilder();
        builder.AppendLine("*📊 Harcama Özeti*");
        builder.AppendLine();
        builder.AppendLine($"Toplam Harcama: {FormatMoney(overview.TotalAmount, overallCurrency)}");
        builder.AppendLine($"İşlem Sayısı: {overview.TransactionCount}");
        builder.AppendLine($"Son 30 Gün: {FormatMoney(overview.Last30DaysTotal, overallCurrency)}");
        builder.AppendLine($"Son İşlem: {overview.LastExpenseDate:dd.MM.yyyy}");

        if (overview.StatementPeriods.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("*Ekstre Dönemleri*");

            for (var index = 0; index < overview.StatementPeriods.Count; index++)
            {
                var statement = overview.StatementPeriods[index];
                builder.AppendLine(
                    $"{index + 1}. {statement.StartDate:dd.MM.yyyy} - {statement.EndDate:dd.MM.yyyy} • {statement.TransactionCount} işlem • {FormatMoney(statement.TotalAmount, statement.Currency)}");
            }

            builder.AppendLine();
            builder.AppendLine("Detay için: `/expense 1`");
        }

        if (overview.Categories.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("*Kategoriler*");

            foreach (var category in overview.Categories)
            {
                builder.AppendLine(
                    $"{EscapeMarkdown(category.Category)} • {category.TransactionCount} işlem • {FormatMoney(category.TotalAmount, category.Currency)}");
            }

            builder.AppendLine();
            builder.AppendLine("Kategori filtresi: `/expense 1 Other`");
        }

        var message = builder.ToString().TrimEnd();

        if (!includeChatDirection)
        {
            return message;
        }

        return $$"""
                 {{message}}

                 Harcama sorularını normal sohbette sorabilirsin.
                 """;
    }

    private async Task<string> BuildStatementDetailMessageAsync(
        int userId,
        int statementNumber,
        string? categoryFilter,
        CancellationToken cancellationToken)
    {
        var overview = await expenseStatementBrowseService.GetOverviewAsync(userId, cancellationToken);
        if (overview is null)
        {
            return """
                   Henüz kayıtlı harcama yok.

                   İpucu: Kredi kartı ekstreni (PDF) göndererek harcamalarını otomatik ekleyebilirsin.
                   """;
        }

        if (statementNumber < 1 || statementNumber > overview.StatementPeriods.Count)
        {
            return BuildInvalidStatementNumberMessage(statementNumber, overview.StatementPeriods.Count);
        }

        var selectedStatement = overview.StatementPeriods[statementNumber - 1];
        var statementDetail = await expenseStatementBrowseService.GetStatementDetailAsync(
            userId,
            selectedStatement.Fingerprint,
            categoryFilter,
            cancellationToken);

        if (statementDetail is null)
        {
            return "İstenen ekstre bulunamadı. Lütfen tekrar dene.";
        }

        if (!string.IsNullOrWhiteSpace(categoryFilter) && statementDetail.AppliedCategory is null)
        {
            return BuildInvalidCategoryMessage(statementNumber, categoryFilter, statementDetail.Categories);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"*📄 Ekstre {statementNumber}*");
        builder.AppendLine();
        builder.AppendLine($"Dönem: {statementDetail.StartDate:dd.MM.yyyy} - {statementDetail.EndDate:dd.MM.yyyy}");
        if (!string.IsNullOrWhiteSpace(statementDetail.AppliedCategory))
        {
            builder.AppendLine($"Kategori: {EscapeMarkdown(statementDetail.AppliedCategory)}");
        }
        builder.AppendLine($"Toplam: {FormatMoney(statementDetail.TotalAmount, statementDetail.Currency)}");
        builder.AppendLine($"İşlem Sayısı: {statementDetail.TransactionCount}");

        if (statementDetail.Categories.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("*Kategoriler*");

            foreach (var category in statementDetail.Categories)
            {
                builder.AppendLine(
                    $"{EscapeMarkdown(category.Category)} • {category.TransactionCount} işlem • {FormatMoney(category.TotalAmount, category.Currency)}");
            }
        }

        builder.AppendLine();

        foreach (var transaction in statementDetail.Transactions)
        {
            builder.AppendLine(
                $"{transaction.ExpenseDate:dd.MM.yyyy} • {FormatTransactionDescription(transaction.Description)} • {FormatMoney(transaction.Amount, transaction.Currency)}");
        }

        builder.AppendLine();
        builder.AppendLine("Kategori filtresi: `/expense 1 Other`");

        return builder.ToString().TrimEnd();
    }

    private static string ExtractUserInput(string? messageText)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return string.Empty;
        }

        var trimmed = messageText.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return trimmed;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? string.Empty : parts[1];
    }

    private static bool TryParseStatementRequest(string userInput, out int statementNumber, out string categoryFilter)
    {
        statementNumber = 0;
        categoryFilter = string.Empty;

        if (string.IsNullOrWhiteSpace(userInput))
        {
            return false;
        }

        var parts = userInput.Trim()
            .Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (!int.TryParse(parts[0], out statementNumber))
        {
            return false;
        }

        categoryFilter = parts.Length > 1 ? parts[1] : string.Empty;
        return true;
    }

    private static string BuildInvalidStatementNumberMessage(int requestedNumber, int statementCount)
    {
        if (statementCount <= 0)
        {
            return """
                   Henüz kayıtlı harcama yok.

                   İpucu: Kredi kartı ekstreni (PDF) göndererek harcamalarını otomatik ekleyebilirsin.
                   """;
        }

        var builder = new StringBuilder();
        builder.AppendLine("*Geçersiz Ekstre Numarası*");
        builder.AppendLine();
        builder.AppendLine($"`/expense {requestedNumber}` bulunamadı.");

        if (statementCount == 1)
        {
            builder.AppendLine("Yalnızca 1 ekstre dönemi var.");
            builder.AppendLine("Detay için: `/expense 1`");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"1 ile {statementCount} arasında bir numara kullan.");
        builder.AppendLine("Örnek: `/expense 1`");
        return builder.ToString().TrimEnd();
    }

    private static string BuildInvalidCategoryMessage(
        int statementNumber,
        string requestedCategory,
        IReadOnlyList<ExpenseCategorySummary> categories)
    {
        var builder = new StringBuilder();
        builder.AppendLine("*Geçersiz Kategori*");
        builder.AppendLine();
        builder.AppendLine($"`{EscapeMarkdown(requestedCategory)}` bulunamadı.");

        if (categories.Count > 0)
        {
            builder.AppendLine("Bu ekstre için kategoriler:");

            foreach (var category in categories)
            {
                builder.AppendLine($"- {EscapeMarkdown(category.Category)}");
            }

            builder.AppendLine($"Örnek: `/expense {statementNumber} {EscapeMarkdown(categories[0].Category)}`");
        }

        return builder.ToString().TrimEnd();
    }

    private static string DetermineOverallCurrencyLabel(IReadOnlyList<ExpenseStatementPeriod> statements)
    {
        var distinctCurrencies = statements
            .Select(x => x.Currency)
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

    private static string FormatMoney(decimal amount, string currency)
    {
        return $"{amount.ToString("N2", TurkishCulture)} {currency}";
    }

    private static string FormatTransactionDescription(string value)
    {
        return PreventTelegramAutoLinks(EscapeMarkdown(value));
    }

    private static string EscapeMarkdown(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character is '\\' or '_' or '*' or '[' or ']' or '`')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string PreventTelegramAutoLinks(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Prevent Telegram clients from turning merchant domains into clickable links.
        return value
            .Replace("/", "/\u200B", StringComparison.Ordinal)
            .Replace(".", ".\u200B", StringComparison.Ordinal);
    }
}
