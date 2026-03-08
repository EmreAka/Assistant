using Assistant.Api.Data;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Services.Concretes;

public class ExpenseCommand(
    IExpenseAnalysisService expenseAnalysisService,
    ApplicationDbContext dbContext,
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

        // 2. Metin Komutu (Özet Bilgi)
        var user = await dbContext.TelegramUsers
            .FirstOrDefaultAsync(u => u.ChatId == message.Chat.Id, cancellationToken);

        if (user == null)
        {
            await client.SendMessage(message.Chat.Id, "Önce /start komutu ile kaydolmalısınız.", cancellationToken: cancellationToken);
            return;
        }

        var totalExpenses = await dbContext.Expenses
            .Where(x => x.TelegramUserId == user.Id)
            .SumAsync(x => x.Amount, cancellationToken);

        await client.SendMessage(
            chatId: message.Chat.Id,
            text: $"""
                  📊 Harcama Özeti
                  
                  Toplam Harcama: {totalExpenses:N2} TRY
                  
                  💡 İpucu: Kredi kartı ekstreni (PDF) buraya göndererek otomatik harcama kaydı yapabilirsin!
                  """,
            cancellationToken: cancellationToken
        );
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
}
