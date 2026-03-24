using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class WebSearchToolFunctions(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> aiOptions,
    ILogger<WebSearchToolFunctions> logger
)
{
    [Description("Searches the public web for fresh information when the question depends on recent events, news, prices, schedules, releases, or other time-sensitive facts.")]
    public async Task<string> SearchWeb(
        [Description("A concise search query describing the information needed. Include names, dates, locations, or other specifics when relevant.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: query is required.";
        }

        try
        {
            using var client = httpClientFactory.CreateClient(BotServiceRegistration.OpenRouterHttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = aiOptions.Value.Model,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = BuildSearchPrompt(query)
                        }
                    },
                    plugins = new[]
                    {
                        new
                        {
                            id = "web",
                            /* engine = "native", */
                            max_results = 1
                        }
                    },
                    temperature = 0.2
                })
            };

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SearchWeb failed with status code {StatusCode} for query: {Query}",
                    response.StatusCode,
                    query);
                return "Web search failed. Continue without fresh web results unless the user asks you to try again.";
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var jsonDocument = await JsonDocument.ParseAsync(responseStream);
            var result = ExtractMessageContent(jsonDocument.RootElement)?.Trim();

            if (string.IsNullOrWhiteSpace(result))
            {
                logger.LogInformation("SearchWeb returned no text for query: {Query}", query);
                return "No useful web results were found.";
            }

            logger.LogInformation("SearchWeb completed for query: {Query}", query);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SearchWeb failed for query: {Query}", query);
            return "Web search failed. Continue without fresh web results unless the user asks you to try again.";
        }
    }

    private static string BuildSearchPrompt(string query)
    {
        return $$"""
                 Gather current public web information needed to answer the query.
                 Return a concise factual summary for another assistant to use.
                 Include only details directly relevant to the query.
                 If dates matter, include exact dates.
                 If the results are mixed or uncertain, say so briefly.

                 Query: {{query}}
                 """;
    }

    private static string? ExtractMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join(
                "\n",
                content.EnumerateArray()
                    .Select(ExtractContentPartText)
                    .Where(static text => !string.IsNullOrWhiteSpace(text))),
            _ => null
        };
    }

    private static string? ExtractContentPartText(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (part.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
        {
            return directText.GetString();
        }

        if (part.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
            && part.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        return null;
    }
}
