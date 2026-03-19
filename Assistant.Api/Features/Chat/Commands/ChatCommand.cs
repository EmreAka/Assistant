using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Features.Chat.Commands;

public class ChatCommand(
    IAgentService agentService,
    ITelegramResponseSender responseSender,
    ILogger<ChatCommand> logger
) : IBotCommand
{
    public string Command => "chat";
    public string Description => "Asistanla sohbet eder.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken)
    {
        var chatId = update.Message?.Chat.Id;
        var messageText = update.Message?.Text;

        if (chatId == null || string.IsNullOrWhiteSpace(messageText))
            return;

        // /chat komutunu temizle
        var userInput = messageText.StartsWith("/chat", StringComparison.OrdinalIgnoreCase)
            ? messageText["/chat".Length..].Trim()
            : messageText;

        if (string.IsNullOrWhiteSpace(userInput))
        {
            await client.SendMessage(
                chatId: chatId,
                text: "Buyur, seni dinliyorum! Bir şeyler yazabilirsin.",
                cancellationToken: cancellationToken
            );
            return;
        }

        try
        {
            var responseText = await agentService.RunAsync(chatId.Value, userInput, cancellationToken: cancellationToken);
            await responseSender.SendResponseAsync(chatId.Value, responseText, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat command execution failed.");
            await client.SendMessage(
                chatId: chatId,
                text: "Bir hata oluştu, lütfen tekrar dener misin?",
                cancellationToken: cancellationToken
            );
        }
    }
}
