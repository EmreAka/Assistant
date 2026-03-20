namespace Assistant.Api.Domain.Configurations;

public class AiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "google/gemini-3.1-flash-lite-preview";
    public string DefaultTimeZoneId { get; set; } = "Europe/Istanbul";
}
