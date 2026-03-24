using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.ChatFeatures;

public class WebSearchToolFunctionsTests
{
    [Fact]
    public async Task SearchWeb_ReturnsError_WhenQueryIsBlank()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await service.SearchWeb("   ");

        Assert.Equal("Error: query is required.", result);
    }

    [Fact]
    public async Task SearchWeb_ReturnsTrimmedText_AndSendsExpectedOpenRouterPayload()
    {
        string? capturedRequestUri = null;
        string? capturedRequestJson = null;
        var service = CreateService(request =>
        {
            capturedRequestUri = request.RequestUri!.ToString();
            capturedRequestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                                            {
                                              "choices": [
                                                {
                                                  "message": {
                                                    "content": "  Fresh result summary.  "
                                                  }
                                                }
                                              ]
                                            }
                                            """)
            };
        });

        var result = await service.SearchWeb("latest ai news");

        Assert.Equal("Fresh result summary.", result);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", capturedRequestUri);

        using var document = JsonDocument.Parse(capturedRequestJson!);
        var root = document.RootElement;

        Assert.Equal("google/gemini-3.1-flash-lite-preview", root.GetProperty("model").GetString());
        Assert.Equal(0.2m, root.GetProperty("temperature").GetDecimal());
        Assert.Equal("web", root.GetProperty("plugins")[0].GetProperty("id").GetString());
        Assert.Equal(
            """
            Gather current public web information needed to answer the query.
            Return a concise factual summary for another assistant to use.
            Include only details directly relevant to the query.
            If dates matter, include exact dates.
            If the results are mixed or uncertain, say so briefly.

            Query: latest ai news
            """,
            root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SearchWeb_ReturnsNoUsefulResults_WhenContentIsMissing()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                                        {
                                          "choices": [
                                            {
                                              "message": {
                                                "content": "   "
                                              }
                                            }
                                          ]
                                        }
                                        """)
        });

        var result = await service.SearchWeb("weather");

        Assert.Equal("No useful web results were found.", result);
    }

    [Fact]
    public async Task SearchWeb_ReturnsFailureMessage_WhenHttpRequestFails()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await service.SearchWeb("bitcoin price");

        Assert.Equal("Web search failed. Continue without fresh web results unless the user asks you to try again.", result);
    }

    [Fact]
    public async Task SearchWeb_ReturnsFailureMessage_WhenResponseCannotBeParsed()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json}", Encoding.UTF8, "application/json")
        });

        var result = await service.SearchWeb("stock market");

        Assert.Equal("Web search failed. Continue without fresh web results unless the user asks you to try again.", result);
    }

    private static WebSearchToolFunctions CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new WebSearchToolFunctions(
            new StubHttpClientFactory(handler),
            Options.Create(new AiOptions
            {
                ApiKey = "test-key",
                ApiUrl = "https://openrouter.ai/api/v1",
                Model = "google/gemini-3.1-flash-lite-preview"
            }),
            NullLogger<WebSearchToolFunctions>.Instance);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubHttpMessageHandler(handler))
            {
                BaseAddress = new Uri("https://openrouter.ai/api/v1/")
            };
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
