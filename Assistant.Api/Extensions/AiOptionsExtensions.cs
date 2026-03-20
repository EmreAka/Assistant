using System.ClientModel;
using Assistant.Api.Domain.Configurations;
using OpenAI;

namespace Assistant.Api.Extensions;

public static class AiOptionsExtensions
{
    public static OpenAIClient CreateOpenAiClient(this AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("AI:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiUrl))
        {
            throw new InvalidOperationException("AI:ApiUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("AI:Model is not configured.");
        }

        return new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.ApiUrl, UriKind.Absolute)
            });
    }
}
