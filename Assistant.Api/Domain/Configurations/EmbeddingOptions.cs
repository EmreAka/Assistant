namespace Assistant.Api.Domain.Configurations;

public class EmbeddingOptions
{
    public string Model { get; set; } = "google/gemini-embedding-2";
    public int Dimensions { get; set; } = 768;
    public int TurnsThreshold { get; set; } = 20;
    public int MaxTurnsPerRun { get; set; } = 50;
<<<<<<< HEAD
    public int SearchCandidates { get; set; } = 10;
    public double MaxCosineDistance { get; set; } = 0.5;
=======
>>>>>>> 711520c (feat: add EmbeddingOptions configuration and related appsettings for chat-turn embeddings)
}
