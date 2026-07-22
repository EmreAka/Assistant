using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Commands;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Commands;
using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Services.Abstracts;
using Assistant.Api.Services.Concretes;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Telegram.Bot;

namespace Assistant.Api.Extensions;

public static class BotServiceRegistration
{
    public const string MarkitdownHttpClientName = "Markitdown";
    public const string OpenRouterHttpClientName = "OpenRouter";

    public static IServiceCollection AddBotServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AiProvidersOptions>(configuration.GetSection("AIProviders"));
        services.Configure<BotOptions>(configuration.GetSection("Bot"));
        services.Configure<MemoryConsolidationOptions>(configuration.GetSection("MemoryConsolidation"));

        services.AddHttpClient(OpenRouterHttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AiProvidersOptions>>().Value.OpenRouter;

            if (!string.IsNullOrWhiteSpace(options.ApiUrl))
            {
                client.BaseAddress = new Uri($"{options.ApiUrl.TrimEnd('/')}/", UriKind.Absolute);
            }

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(45);
        });
        services.AddSingleton<ITelegramBotClient>(provider =>
            new TelegramBotClient(
                provider.GetRequiredService<IOptions<BotOptions>>().Value.BotToken));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAssistantTimeService, AssistantTimeService>();
        services.AddSingleton<ITelegramResponseSender, TelegramResponseSender>();
        services.AddScoped<IDeferredIntentScheduler, DeferredIntentScheduler>();
        services.AddScoped<IChatTurnService, ChatTurnService>();
        services.AddScoped<IPersonalityService, PersonalityService>();
        services.AddScoped<IMemoryService, MemoryService>();
        services.AddScoped<IMemoryConsolidationScheduler, MemoryConsolidationScheduler>();
        services.AddScoped<IMemoryConsolidationCoordinator, MemoryConsolidationCoordinator>();
        services.AddScoped<IMemoryConsolidationAgentService, MemoryConsolidationAgentService>();
        services.AddScoped<IAgentService, AgentService>();

        services.AddTransient<IBotCommand, MemoryCommand>();
        services.AddTransient<IBotCommand, StartCommand>();
        services.AddTransient<IBotCommand, ChatCommand>();
        services.AddTransient<IBotCommandFactory, BotCommandFactory>();
        services.AddTransient<ICommandUpdateHandler, CommandUpdateHandler>();

        return services;
    }
}
