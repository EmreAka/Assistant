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
    ApplicationDbContext dbContext,
    ITelegramResponseSender responseSender,
    ILogger<ExpenseCommand> logger
) : IBotCommand
{
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
        var expenses = await dbContext.Expenses
            .AsNoTracking()
            .Where(x => x.TelegramUserId == userId)
            .OrderByDescending(x => x.ExpenseDate)
            .ToListAsync(cancellationToken);

        if (expenses.Count == 0)
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

        var now = DateTime.UtcNow.Date;
        var last30Start = now.AddDays(-30);
        var last30Total = expenses
            .Where(x => x.ExpenseDate.Date >= last30Start && x.ExpenseDate.Date <= now)
            .Sum(x => x.Amount);

        var message = $"""
                       📊 Harcama Özeti

                       Toplam Harcama: {expenses.Sum(x => x.Amount):N2} TRY
                       İşlem Sayısı: {expenses.Count}
                       Son 30 Gün: {last30Total:N2} TRY
                       Son İşlem: {expenses.Max(x => x.ExpenseDate):dd.MM.yyyy}
                       """;

        if (!includeChatDirection)
        {
            return message;
        }

        return $$"""
                 {{message}}

                 Harcama sorularını normal sohbette sorabilirsin.
                 """;
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
}
