using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Abstracts;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Extensions;

public static class BotApplicationBuilder
{
    public static async Task UseBotAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();
        var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
        var botOptions = scope.ServiceProvider.GetRequiredService<IOptions<BotOptions>>().Value;
        var botCommandFactory = scope.ServiceProvider.GetRequiredService<IBotCommandFactory>();

        var commands = botCommandFactory.GetAllCommands()
            .Select(c => new BotCommand
            {
                Command = c.Command,
                Description = c.Description
            });

        await botClient.SetMyCommands(commands, cancellationToken: cancellationToken);
        await botClient.SetWebhook(
            botOptions.WebhookUrl,
            secretToken: botOptions.SecretToken,
            cancellationToken: cancellationToken);
    }
}
