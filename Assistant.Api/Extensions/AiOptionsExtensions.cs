using System.ClientModel;
using Assistant.Api.Domain.Configurations;
using Google.GenAI;
using OpenAI;

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
                Endpoint = new Uri(options.ApiUrl, UriKind.Absolute)
            });
    }

    public static Client CreateGoogleGenAIClient(this GoogleAiStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("AIProviders:GoogleGenAI:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("AIProviders:GoogleGenAI:Model is not configured.");
        }

        return new Client(apiKey: options.ApiKey);
    }
}
