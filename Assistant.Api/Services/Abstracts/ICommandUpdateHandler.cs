using Telegram.Bot.Types;

namespace Assistant.Api.Services.Abstracts;

public interface ICommandUpdateHandler
{
    Task HandleAsync(
        Update update,
        CancellationToken cancellationToken
    );
}
