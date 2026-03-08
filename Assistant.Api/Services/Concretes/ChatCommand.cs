using System.Collections.Concurrent;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Abstracts;
using Google.GenAI;
using OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using System.ClientModel;

namespace Assistant.Api.Services.Concretes;

public class ChatCommand(
    IPersonalityService personalityService,
    IOptions<AiOptions> aiOptions,
    ILogger<ChatCommand> logger
) : IBotCommand
{
    private readonly AiOptions _aiOptions = aiOptions.Value;

    // Geçici olarak sessionları hafızada tutalım. 
    private static readonly ConcurrentDictionary<long, AgentSession> Sessions = new();

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
            //var geminiClient = new Client(apiKey: _aiOptions.GoogleApiKey);
            OpenAIClient openAIChatClient = new(
                new ApiKeyCredential(_aiOptions.GrokApiKey),
                new OpenAIClientOptions() { Endpoint = new Uri(_aiOptions.GrokApiBaseUrl) }
            );

            var chatClientNew = openAIChatClient
                .GetChatClient("grok-4-1-fast-reasoning")
                .AsIChatClient();

            /* var chatClient = geminiClient
                .AsIChatClient(_aiOptions.Model)
                .AsBuilder()
                .Build(); */

            var agent = new ChatClientAgent(
                chatClientNew,
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Temperature = 1,
                        ModelId = "grok-4-1-fast-reasoning",
                    },
                    AIContextProviders = [new PersonalityContextProvider(chatId.Value, personalityService)],
                    //ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = new MessageCountingChatReducer(50) })
#pragma warning disable MEAI001
                    ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = new SummarizingChatReducer(chatClientNew, 30, 10) })
#pragma warning restore MEAI001
                }
            );

            // Chat bazlı session'ı al veya oluştur
            if (!Sessions.TryGetValue(chatId.Value, out var session))
            {
                session = await agent.CreateSessionAsync(cancellationToken);
                Sessions[chatId.Value] = session;
            }

            var response = await agent.RunAsync(userInput, session, cancellationToken: cancellationToken);
            var responseText = response.Text?.Trim() ?? "Üzgünüm, şu an cevap veremiyorum.";

            await client.SendMessage(
                chatId: chatId,
                text: responseText,
                cancellationToken: cancellationToken
            );
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
