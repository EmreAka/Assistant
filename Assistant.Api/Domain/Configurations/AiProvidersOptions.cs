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
    public string Model { get; set; } = "google/gemini-3.1-flash-lite-preview";
}

public class GoogleAiStudioOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3.1-flash-lite-preview";
}

public class XAIOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = "https://api.x.ai/v1";
    public string Model { get; set; } = "grok-4-1-fast-reasoning";
}