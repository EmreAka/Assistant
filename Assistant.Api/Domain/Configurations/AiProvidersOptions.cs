using Microsoft.Extensions.AI;

namespace Assistant.Api.Domain.Configurations;

public class AiProvidersOptions
{
    public OpenRouterOptions OpenRouter { get; set; } = new();
    public GoogleAiStudioOptions GoogleAIStudio { get; set; } = new();
    public XAIOptions XAI { get; set; } = new();
    public string DefaultTimeZoneId { get; set; } = "Europe/Istanbul";
}

public class OpenRouterOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "google/gemini-3.1-flash-lite";
    public OpenRouterWebSearchOptions WebSearch { get; set; } = new();
    public OpenRouterReasoningOptions Reasoning { get; set; } = new();
}

/// <summary>
/// Reasoning effort per agent, applied through <see cref="ChatOptions.Reasoning"/>.
/// Allowed values: None, Low, Medium, High, ExtraHigh. Null leaves the model default in place.
/// </summary>
public class OpenRouterReasoningOptions
{
    /// <summary>
    /// Interactive chat and deferred task runs. Needs enough reasoning for tool calls, dates and
    /// cron expressions, while staying fast enough for a Telegram reply.
    /// </summary>
    public ReasoningEffort Chat { get; set; } = ReasoningEffort.Medium;

    /// <summary>
    /// Background memory merge. Runs rarely and off the reply path, and has to weigh contradictions
    /// and many keep/drop rules, so it gets the most reasoning.
    /// </summary>
    public ReasoningEffort MemoryConsolidation { get; set; } = ReasoningEffort.High;

    /// <summary>
    /// Chat history compression. A mechanical summary where speed matters more than depth.
    /// </summary>
    public ReasoningEffort ChatSummarization { get; set; } = ReasoningEffort.Low;
}

/// <summary>
/// Settings for the OpenRouter <c>openrouter:web_search</c> server tool.
/// The model decides when to search; OpenRouter runs the search server-side.
/// </summary>
public class OpenRouterWebSearchOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>auto, native, exa, firecrawl, parallel or perplexity.</summary>
    public string Engine { get; set; } = "auto";

    /// <summary>Maximum results per search call. Ignored by native provider search.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Maximum searches per request. Zero leaves the OpenRouter default in place.</summary>
    public int MaxUses { get; set; } = 3;

    /// <summary>low, medium or high. Empty leaves the engine default in place.</summary>
    public string SearchContextSize { get; set; } = string.Empty;
}

/// <summary>
/// Kept for optional/experimental use. Not part of the active chat, memory or
/// web search path, which all run through OpenRouter.
/// </summary>
public class GoogleAiStudioOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3.1-flash-lite";
}

public class XAIOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = "https://api.x.ai/v1";
    public string Model { get; set; } = "grok-4.3";
    public string TtsVoiceId { get; set; } = "Carina";
    public string TtsLanguage { get; set; } = "en";
}
