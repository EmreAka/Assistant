using Assistant.Api.Services.Abstracts;
using Hangfire;
using Telegram.Bot.Types;

namespace Assistant.Api.Services.Concretes;

[AutomaticRetry(Attempts = 0)]
public class CommandUpdateJob(
    ICommandUpdateHandler commandUpdateHandler,
    ILogger<CommandUpdateJob> logger)
{
    public async Task ExecuteAsync(Update update)
    {
        if (update is null)
        {
            logger.LogWarning("Queued update payload was null.");
            return;
        }

        logger.LogInformation("Processing queued Telegram update. UpdateId: {UpdateId}", update.Id);
        await commandUpdateHandler.HandleAsync(update, CancellationToken.None);
    }
}
