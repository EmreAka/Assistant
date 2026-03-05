namespace Assistant.Api.Domain.Configurations;

public class AiOptions
{
    public string GoogleApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3.1-flash-lite-preview";
    public string DefaultTimeZoneId { get; set; } = "Europe/Istanbul";
}
