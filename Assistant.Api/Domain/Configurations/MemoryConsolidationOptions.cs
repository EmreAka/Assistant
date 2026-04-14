namespace Assistant.Api.Domain.Configurations;

public class MemoryConsolidationOptions
{
    public int TurnsThreshold { get; set; } = 20;
    public int StaleJobAfterMinutes { get; set; } = 15;
}
