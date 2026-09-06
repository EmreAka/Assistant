using System.ClientModel.Primitives;
using System.Text.Json;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using OpenAI.Chat;

namespace Assistant.Api.Tests.Extensions;

public class AiOptionsExtensionsTests
{
    [Fact]
    public void CreateRawChatCompletionOptions_AppendsWebSearchServerTool()
    {
        var options = new OpenRouterOptions
        {
            WebSearch = new OpenRouterWebSearchOptions
            {
                Enabled = true,
                Engine = "exa",
                MaxResults = 7,
                MaxUses = 2,
                SearchContextSize = "medium"
            }
        };

        var tool = GetTools(options.CreateRawChatCompletionOptions()).Single();

        Assert.Equal("openrouter:web_search", tool.GetProperty("type").GetString());

        var parameters = tool.GetProperty("parameters");
        Assert.Equal("exa", parameters.GetProperty("engine").GetString());
        Assert.Equal(7, parameters.GetProperty("max_results").GetInt32());
        Assert.Equal(2, parameters.GetProperty("max_uses").GetInt32());
        Assert.Equal("medium", parameters.GetProperty("search_context_size").GetString());
    }

    [Fact]
    public void CreateRawChatCompletionOptions_OmitsUnsetWebSearchParameters()
    {
        var options = new OpenRouterOptions
        {
            WebSearch = new OpenRouterWebSearchOptions
            {
                Engine = "auto",
                MaxResults = 0,
                MaxUses = 0,
                SearchContextSize = string.Empty
            }
        };

        var parameters = GetTools(options.CreateRawChatCompletionOptions()).Single().GetProperty("parameters");

        Assert.Equal("auto", parameters.GetProperty("engine").GetString());
        Assert.False(parameters.TryGetProperty("max_results", out _));
        Assert.False(parameters.TryGetProperty("max_uses", out _));
        Assert.False(parameters.TryGetProperty("search_context_size", out _));
    }

    [Fact]
    public void CreateRawChatCompletionOptions_SkipsWebSearchToolWhenDisabled()
    {
        var options = new OpenRouterOptions
        {
            WebSearch = new OpenRouterWebSearchOptions { Enabled = false }
        };

        Assert.Empty(GetTools(options.CreateRawChatCompletionOptions()));
    }

    [Fact]
    public void CreateRawChatCompletionOptions_KeepsFunctionToolsAheadOfTheServerTool()
    {
        var rawOptions = new OpenRouterOptions().CreateRawChatCompletionOptions();
        rawOptions.Tools.Add(ChatTool.CreateFunctionTool(
            "GetCurrentDateTime",
            "Gets the current date and time.",
            BinaryData.FromString("""{"type":"object","properties":{}}""")));

        var tools = GetTools(rawOptions);

        Assert.Equal(2, tools.Length);
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("openrouter:web_search", tools[1].GetProperty("type").GetString());
    }

    private static JsonElement[] GetTools(ChatCompletionOptions rawOptions)
    {
        var payload = JsonDocument.Parse(ModelReaderWriter.Write(rawOptions, ModelReaderWriterOptions.Json));

        return payload.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().ToArray()
            : [];
    }
}
