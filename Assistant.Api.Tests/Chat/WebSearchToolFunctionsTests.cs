using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.ChatFeatures;

/// <summary>
/// WebSearchToolFunctions now calls Google Gen AI directly via the Gen AI SDK
/// instead of sending raw HTTP through IHttpClientFactory.
/// The old tests that inspected exact OpenRouter HTTP payloads are obsolete.
/// Integration tests with real API keys would cover end-to-end web search.
/// </summary>
public class WebSearchToolFunctionsTests
{
    [Fact]
    public async Task SearchWeb_ReturnsError_WhenQueryIsBlank()
    {
        var service = CreateService();

        var result = await service.SearchWeb("   ");

        Assert.Equal("Error: query is required.", result);
    }

    private static WebSearchToolFunctions CreateService()
    {
        return new WebSearchToolFunctions(
            Options.Create(new AiProvidersOptions
            {
                GoogleAIStudio = new GoogleAiStudioOptions
                {
                    ApiKey = "test-key",
                    Model = "gemini-3.1-flash-lite-preview"
                }
            }),
            NullLogger<WebSearchToolFunctions>.Instance);
    }
}