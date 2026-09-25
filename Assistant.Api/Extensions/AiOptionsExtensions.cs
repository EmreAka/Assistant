using System.ClientModel;
using System.Text.Json;
using Assistant.Api.Domain.Configurations;
using Google.GenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Assistant.Api.Extensions;

public static class AiOptionsExtensions
{
    public static OpenAIClient CreateOpenAiClient(this OpenRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("AIProviders:OpenRouter:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiUrl))
        {
            throw new InvalidOperationException("AIProviders:OpenRouter:ApiUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("AIProviders:OpenRouter:Model is not configured.");
        }

        return new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.ApiUrl, UriKind.Absolute),
                // Carries the 45s timeout that used to be configured on the (unused) named
                // "OpenRouter" HttpClient. The SDK applies this per network operation.
                NetworkTimeout = TimeSpan.FromSeconds(45)
            });
    }

    public static IChatClient CreateOpenRouterChatClient(this OpenRouterOptions options)
    {
        // Callers should reuse the returned client for the process lifetime (BotServiceRegistration
        // registers it as a singleton); the OpenAI SDK clients are thread-safe and are not meant to
        // be created per request.
        return options.CreateOpenAiClient()
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    public static IEmbeddingGenerator<string, Embedding<float>> CreateOpenRouterEmbeddingGenerator(
        this OpenRouterOptions options,
        string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Embeddings:Model is not configured.");
        }

        return options.CreateOpenAiClient()
            .GetEmbeddingClient(model)
            .AsIEmbeddingGenerator();
    }

    /// <summary>
    /// Builds the provider-specific request payload for a chat turn. OpenRouter server tools
    /// (such as <c>openrouter:web_search</c>) are not part of the OpenAI wire format, so they are
    /// patched into the outgoing <c>tools</c> array alongside the locally declared function tools.
    /// </summary>
    public static ChatCompletionOptions CreateRawChatCompletionOptions(this OpenRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rawOptions = new ChatCompletionOptions();

        if (options.WebSearch.Enabled)
        {
#pragma warning disable SCME0001
            rawOptions.Patch.Append("$.tools"u8, BuildWebSearchServerTool(options.WebSearch));
#pragma warning restore SCME0001
        }

        return rawOptions;
    }

    private static BinaryData BuildWebSearchServerTool(OpenRouterWebSearchOptions options)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "openrouter:web_search");

            writer.WriteStartObject("parameters");

            if (!string.IsNullOrWhiteSpace(options.Engine))
            {
                writer.WriteString("engine", options.Engine);
            }

            if (options.MaxResults > 0)
            {
                writer.WriteNumber("max_results", options.MaxResults);
            }

            if (options.MaxUses > 0)
            {
                writer.WriteNumber("max_uses", options.MaxUses);
            }

            if (!string.IsNullOrWhiteSpace(options.SearchContextSize))
            {
                writer.WriteString("search_context_size", options.SearchContextSize);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new BinaryData(buffer.ToArray());
    }

    /// <summary>
    /// Kept for optional/experimental use. The active chat, memory and web search
    /// paths all run through OpenRouter.
    /// </summary>
    public static Client CreateGoogleGenAIClient(this GoogleAiStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("AIProviders:GoogleAIStudio:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("AIProviders:GoogleAIStudio:Model is not configured.");
        }

        return new Client(apiKey: options.ApiKey);
    }

    public static IChatClient CreateGoogleGenAIChatClient(this GoogleAiStudioOptions options)
    {
        return options.CreateGoogleGenAIClient()
            .AsIChatClient(options.Model);
    }

    public static OpenAIClient CreateXAIClient(this XAIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("AIProviders:XAI:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("AIProviders:XAI:Model is not configured.");
        }

        return new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.ApiUrl, UriKind.Absolute)
            });
    }

    public static IChatClient CreateXAIChatClient(this XAIOptions options)
    {
        return options.CreateXAIClient()
            .GetChatClient(options.Model)
            .AsIChatClient();
    }
}
