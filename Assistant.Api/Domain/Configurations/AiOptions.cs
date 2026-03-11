namespace Assistant.Api.Domain.Configurations;

public class AiOptions
{
    public string GoogleApiKey { get; set; } = string.Empty;
    public string GrokApiKey { get; set; } = string.Empty;
    public string GrokApiBaseUrl { get; set; } = "https://api.x.ai/v1";
    public string Model { get; set; } = "gemini-3.1-flash-lite-preview";
    public float AgentTemperature { get; set; } = 0.7f;
    public int AgentHistoryRecentTokenBudget { get; set; } = 120_000;
    public int AgentHistorySummarizeTriggerTokenBudget { get; set; } = 180_000;
    public string DefaultTimeZoneId { get; set; } = "Europe/Istanbul";
}
