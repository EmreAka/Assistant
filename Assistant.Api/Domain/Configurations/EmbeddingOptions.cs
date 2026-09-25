namespace Assistant.Api.Domain.Configurations;

public class EmbeddingOptions
{
    public string Model { get; set; } = "google/gemini-embedding-2";
    public int Dimensions { get; set; } = 768;
    public int TurnsThreshold { get; set; } = 20;
    public int MaxTurnsPerRun { get; set; } = 50;
}
