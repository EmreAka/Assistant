using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Services.Concretes;

public class StartCommand : IBotCommand
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

        await client.SendMessage(
            chatId: chatId,
            text: "Hoş geldiniz! Kişisel asistan başlatıldı.",
            cancellationToken: cancellationToken
        );
    }
}
