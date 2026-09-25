using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Commands;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Commands;
using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Services.Abstracts;
using Assistant.Api.Services.Concretes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Telegram.Bot;

namespace Assistant.Api.Extensions;

public static class BotServiceRegistration
{
    public const string MarkitdownHttpClientName = "Markitdown";
    public const string XAiHttpClientName = "XAI";

    public static IServiceCollection AddBotServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AiProvidersOptions>(configuration.GetSection("AIProviders"));
        services.Configure<BotOptions>(configuration.GetSection("Bot"));
        services.Configure<MemoryConsolidationOptions>(configuration.GetSection("MemoryConsolidation"));
        services.Configure<EmbeddingOptions>(configuration.GetSection("Embeddings"));

        // NOTE: the named "OpenRouter" HttpClient registration was removed here. No code path ever
        // resolved it (the OpenAI SDK builds its own transport), so its configuration - including the
        // 45s timeout - was dead. That timeout now lives on OpenAIClientOptions.NetworkTimeout.

        services.AddHttpClient(XAiHttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AiProvidersOptions>>().Value.XAI;

            if (!string.IsNullOrWhiteSpace(options.ApiUrl))
            {
                client.BaseAddress = new Uri($"{options.ApiUrl.TrimEnd('/')}/", UriKind.Absolute);
            }

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(60);
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
        // One shared OpenRouter client for the whole process. Building an OpenAIClient per message
        // created a fresh SDK HTTP pipeline/connection pool for each turn and then disposed it;
        // reusing the client keeps connections warm and makes disposal a container-shutdown concern.
        // Registered as IChatClient so both AgentService and MemoryConsolidationAgentService share it.
        services.AddSingleton<IChatClient>(provider =>
            provider.GetRequiredService<IOptions<AiProvidersOptions>>().Value.OpenRouter.CreateOpenRouterChatClient());

        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<ITextToSpeechService, XaiTextToSpeechService>();

        services.AddTransient<IBotCommand, MemoryCommand>();
        services.AddTransient<IBotCommand, StartCommand>();
        services.AddTransient<IBotCommand, ChatCommand>();
        services.AddTransient<IBotCommand, TtsCommand>();
        services.AddTransient<IBotCommandFactory, BotCommandFactory>();
        services.AddTransient<ICommandUpdateHandler, CommandUpdateHandler>();

        return services;
    }
}
