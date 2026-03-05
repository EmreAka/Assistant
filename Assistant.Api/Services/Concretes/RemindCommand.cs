using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Services.Concretes;

public class RemindCommand(
    IReminderAgentService reminderAgentService
) : IBotCommand
{
    public string Command => "remind";
    public string Description => "Doğal dil ile hatırlatma oluşturur.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken
    )
    {
        var message = update.Message;
        if (message?.Chat is null || message.Type != MessageType.Text)
        {
            return;
        }

        if (message.Chat.Type != ChatType.Private)
        {
            await client.SendMessage(
                chatId: message.Chat.Id,
                text: "Bu komut şimdilik sadece private chat'te destekleniyor.",
                cancellationToken: cancellationToken
            );
            return;
        }

        var payload = ExtractPayload(message.Text);
        if (string.IsNullOrWhiteSpace(payload))
        {
            await client.SendMessage(
                chatId: message.Chat.Id,
                text: """
                      Kullanım: /remind <hatırlatma isteği>

                      Örnek:
                      /remind 5 saat sonra Mustafa abiyle toplantımı hatırlat
                      """,
                cancellationToken: cancellationToken
            );
            return;
        }

        var result = await reminderAgentService.ProcessReminderAsync(
            message.Chat.Id,
            payload,
            cancellationToken
        );

        await client.SendMessage(
            chatId: message.Chat.Id,
            text: result.UserMessage,
            cancellationToken: cancellationToken
        );
    }

    private static string ExtractPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return parts[1].Trim();
    }
}
