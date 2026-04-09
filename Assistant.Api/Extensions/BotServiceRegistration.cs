using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Commands;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.Expense.Commands;
using Assistant.Api.Features.Expense.Services;
using Assistant.Api.Features.Tefas.Commands;
using Assistant.Api.Features.Tefas.Services;
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
    public const string TefasHttpClientName = "Tefas";

    public static IServiceCollection AddBotServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AiProvidersOptions>(configuration.GetSection("AIProviders"));
        services.Configure<BotOptions>(configuration.GetSection("Bot"));

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
        services.AddHttpClient(TefasHttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://www.tefas.gov.tr/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AssistantBot/1.0");
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddSingleton<ITelegramBotClient>(provider =>
            new TelegramBotClient(
                provider.GetRequiredService<IOptions<BotOptions>>().Value.BotToken));

        services.AddSingleton<ITelegramResponseSender, TelegramResponseSender>();
        services.AddScoped<IDeferredIntentScheduler, DeferredIntentScheduler>();
        services.AddScoped<IChatTurnService, ChatTurnService>();
        services.AddScoped<IPersonalityService, PersonalityService>();
        services.AddScoped<IMemoryService, MemoryService>();
        services.AddScoped<IExpenseAnalysisService, ExpenseAnalysisService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<ITefasHtmlParser, TefasHtmlParser>();
        services.AddScoped<ITefasAnalysisService, TefasAnalysisService>();

        services.AddTransient<IBotCommand, ExpenseCommand>();
        services.AddTransient<IBotCommand, StartCommand>();
        services.AddTransient<IBotCommand, ChatCommand>();
        services.AddTransient<IBotCommand, TefasCommand>();
        services.AddTransient<IBotCommandFactory, BotCommandFactory>();
        services.AddTransient<ICommandUpdateHandler, CommandUpdateHandler>();

        return services;
    }
}
