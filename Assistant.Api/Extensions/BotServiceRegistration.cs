using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Abstracts;
using Assistant.Api.Services.Concretes;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Assistant.Api.Extensions;

public static class BotServiceRegistration
{
    public static IServiceCollection AddBotServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BotOptions>(configuration.GetSection("Bot"));

        services.AddSingleton<ITelegramBotClient>(provider =>
            new TelegramBotClient(
                provider.GetRequiredService<IOptions<BotOptions>>().Value.BotToken));

        services.AddTransient<IBotCommand, StartCommand>();
        services.AddTransient<IBotCommandFactory, BotCommandFactory>();
        services.AddTransient<ICommandUpdateHandler, CommandUpdateHandler>();

        return services;
    }
}
