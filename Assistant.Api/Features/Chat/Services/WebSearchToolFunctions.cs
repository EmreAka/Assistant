using System.ComponentModel;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class WebSearchToolFunctions(
    IOptions<AiOptions> aiOptions,
    ILogger<WebSearchToolFunctions> logger
)
{
    private const string WebSearchUnavailableToken = "WEB_SEARCH_UNAVAILABLE";

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
            var options = aiOptions.Value;
            var completion = await options
                .CreateOpenAiClient()
                .GetChatClient(options.Model)
                .CompleteChatAsync(BuildSearchPrompt(query));

            var result = string.Concat(completion.Value.Content.Select(static part => part.Text)).Trim();
            if (string.IsNullOrWhiteSpace(result))
            {
                logger.LogInformation("SearchWeb returned no text for query: {Query}", query);
                return "No useful web results were found.";
            }

            if (string.Equals(result, WebSearchUnavailableToken, StringComparison.Ordinal))
            {
                logger.LogInformation("SearchWeb is unavailable for query: {Query}", query);
                return "Web search is unavailable with the current AI provider configuration. Continue without fresh web results unless the user asks to try another source.";
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
                 You are acting as a web-search subtool for another assistant.
                 If you do not have live access to the public web for this request, reply with exactly {{WebSearchUnavailableToken}}.
                 If you do have live web access, gather current public web information needed to answer the query.
                 Return a concise factual summary for another assistant to use.
                 Include only details directly relevant to the query.
                 If dates matter, include exact dates.
                 If the results are mixed or uncertain, say so briefly.

                 Query: {{query}}
                 """;
    }
}
