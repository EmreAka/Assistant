namespace Assistant.Api.Domain.Configurations;

public class AiOptions
{
    public string GoogleApiKey { get; set; } = string.Empty;
    public string GrokApiKey { get; set; } = string.Empty;
    public string GrokApiBaseUrl { get; set; } = "https://api.x.ai/v1";
    public string Model { get; set; } = "gemini-3.1-flash-lite-preview";
    public string EmbeddingModel { get; set; } = "gemini-embedding-2-preview";
    public int EmbeddingDimensions { get; set; } = 768;
    public string MemoryMaintenanceModel { get; set; } = "gemini-3.1-flash-lite-preview";
    public int MemoryArchiveAfterDaysLow { get; set; } = 30;
    public int MemoryArchiveAfterDaysMedium { get; set; } = 90;
    public string MemoryConsolidationCron { get; set; } = "0 3 * * *";
    public string DefaultTimeZoneId { get; set; } = "Europe/Istanbul";
}
