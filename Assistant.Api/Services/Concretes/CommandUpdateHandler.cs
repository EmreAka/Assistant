using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Services.Concretes;

public class CommandUpdateHandler(
    IBotCommandFactory botCommandFactory,
    ITelegramBotClient client,
    ILogger<CommandUpdateHandler> logger
) : ICommandUpdateHandler
{
    public async Task HandleAsync(
        Update update,
        CancellationToken cancellationToken
    )
    {
        var command = GetCommand(update);
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        logger.LogInformation("Received command: {Command}", command);

        var commandHandler = botCommandFactory.GetCommand(command);

        if (commandHandler is null)
        {
            logger.LogWarning("Command not found: {Command}", command);
            return;
        }

        try
        {
            logger.LogInformation("Executing command: {Command}", command);
            await commandHandler.ExecuteAsync(update, client, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "An error occurred while executing command: {Command}", command);
        }
    }

    /* private static string GetCommand(Update update)
    {
        if (update.Type is not UpdateType.Message) return string.Empty;
        if (update.Message?.Type is not MessageType.Text || string.IsNullOrWhiteSpace(update.Message.Text))
            return string.Empty;

        var text = update.Message.Text.Trim();
        if (!text.StartsWith('/')) return string.Empty;

        var firstToken = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]; // /start@Bot
        var commandPart = firstToken[1..]; // start@Bot
        var atIndex = commandPart.IndexOf('@');
        if (atIndex >= 0)
            commandPart = commandPart[..atIndex]; // start

        return commandPart.ToLowerInvariant();
    } */

    private static string GetCommand(Update update)
    {
        if (update.Type is not UpdateType.Message) return string.Empty;
        
        var text = update.Message?.Text ?? update.Message?.Caption;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return "chat";

        var firstToken = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var commandPart = firstToken[1..];
        var atIndex = commandPart.IndexOf('@');
        if (atIndex >= 0)
            commandPart = commandPart[..atIndex];

        return commandPart.ToLowerInvariant();
    }
}
