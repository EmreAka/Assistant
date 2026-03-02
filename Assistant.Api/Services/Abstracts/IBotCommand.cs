using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Services.Abstracts;

public interface IBotCommand
{
    string Command { get; }
    string Description { get; }

    Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken
    );
}
