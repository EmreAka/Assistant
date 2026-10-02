namespace Assistant.Api.Domain.Configurations;

public class EmotionOptions
{
    public bool Enabled { get; set; } = true;

    // OpenRouter model for the mood update, sent per request through ChatOptions.ModelId on the shared
    // chat client. Empty uses AIProviders:OpenRouter:Model.
    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash";

    // The mood with no recent events, and the one it decays back to.
    public double BaselineValence { get; set; } = 0.3;
    public double BaselineArousal { get; set; } = 0.5;
    public string BaselineMood { get; set; } = "relaxed";

    public double HalfLifeHours { get; set; } = 6;

    // Safety cap on a single turn's change, against typos in Events or IntensityMultipliers.
    public double MaxDeltaPerTurn { get; set; } = 0.3;

    // Mood change per event type at medium intensity. The binder adds or overrides keys from config;
    // it can't remove a default.
    public Dictionary<string, EmotionEventEffect> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"] = new() { Valence = 0, Arousal = 0 },
        ["affection"] = new() { Valence = 0.15, Arousal = 0.05 },
        ["playful"] = new() { Valence = 0.10, Arousal = 0.15 },
        ["good_news"] = new() { Valence = 0.15, Arousal = 0.15 },
        ["bad_news"] = new() { Valence = -0.15, Arousal = 0.10 },
        ["cold"] = new() { Valence = -0.05, Arousal = -0.10 },
        ["rude"] = new() { Valence = -0.20, Arousal = 0.15 }
    };

    public Dictionary<string, double> IntensityMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = 0.5,
        ["medium"] = 1.0,
        ["high"] = 1.5
    };
}

public class EmotionEventEffect
{
    public double Valence { get; set; }
    public double Arousal { get; set; }
}
