namespace Assistant.Api.Domain.Configurations;

public class MemoryItemOptions
{
    // OpenRouter model for extraction and reconciliation, sent per request through ChatOptions.ModelId
    // on the shared chat client. Empty uses AIProviders:OpenRouter:Model.
    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash";
    public int TurnsThreshold { get; set; } = 10;
    public int MaxTurnsPerRun { get; set; } = 30;
    public int MaxCandidatesPerRun { get; set; } = 20;
    public int ReconcileTopK { get; set; } = 5;
    public double ReconcileMaxCosineDistance { get; set; } = 0.35;
    public int MaxCoreItems { get; set; } = 40;
    public int RetrievalTopK { get; set; } = 8;
    public double RetrievalMaxCosineDistance { get; set; } = 0.5;
    public int MaxItemLength { get; set; } = 300;
}
