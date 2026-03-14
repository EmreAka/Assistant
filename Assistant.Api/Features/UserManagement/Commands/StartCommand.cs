using Assistant.Api.Data;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.UserManagement.Commands;

public class StartCommand(
    ApplicationDbContext dbContext
) : IBotCommand
{
    public string Command => "start";
    public string Description => "Asistanı başlatır.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken)
    {
        var chatId = update.Message?.Chat.Id;
        if (chatId == null) return;

        var isRegistered = await dbContext.TelegramUsers.AnyAsync(
            x => x.ChatId == chatId.Value,
            cancellationToken
        );

        if (isRegistered)
        {
            await client.SendMessage(
                chatId: chatId,
                text: "Kişisel Asistan zaten başlatıldı.",
                cancellationToken: cancellationToken
            );
            return;
        }

        var from = update.Message?.From;
        var telegramUser = new TelegramUser
        {
            ChatId = chatId.Value,
            CreatedAt = DateTime.UtcNow,
            UserName = from?.Username ?? string.Empty,
            FirstName = from?.FirstName ?? string.Empty,
            LastName = from?.LastName ?? string.Empty
        };

        await dbContext.TelegramUsers.AddAsync(telegramUser, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await client.SendMessage(
            chatId: chatId,
            text: "Hoş geldiniz! Kişisel asistan başlatıldı.",
            cancellationToken: cancellationToken
        );
    }
}
