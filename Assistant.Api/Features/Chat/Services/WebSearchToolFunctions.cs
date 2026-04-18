using System.ComponentModel;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class WebSearchToolFunctions(
    IOptions<AiProvidersOptions> aiProvidersOptions,
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
            var options = aiProvidersOptions.Value.GoogleAIStudio;
            var client = options.CreateGoogleGenAIClient();

            var response = await client.Models.GenerateContentAsync(
                model: options.Model,
                contents: BuildSearchPrompt(query),
                config: new GenerateContentConfig
                {
                    Temperature = 0.2f,
                    Tools =
                    [
                        new Tool
                        {
                            GoogleSearch = new GoogleSearch()
                        }
                    ]
                });

            var result = response.Text?.Trim();
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
        return $"""
                 Gather current public web information needed to answer the query.
                 Return a concise factual summary for another assistant to use.
                 Include only details directly relevant to the query.
                 If dates matter, include exact dates.
                 If the results are mixed or uncertain, say so briefly.

                 Query: {query}
                 """;
    }
}
