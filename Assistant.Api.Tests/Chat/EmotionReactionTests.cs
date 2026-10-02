using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;

namespace Assistant.Api.Tests.ChatFeatures;

public class EmotionReactionTests
{
    private static readonly EmotionOptions Options = new();

    [Theory]
    [InlineData("low", -0.10, 0.075)]
    [InlineData("medium", -0.20, 0.15)]
    [InlineData("high", -0.30, 0.225)]
    public void ToDelta_ScalesEventEffectByIntensity(string intensity, double expectedValence, double expectedArousal)
    {
        var (delta, isValid) = new EmotionReaction("rude", intensity, "annoyed", "user insulted me").ToDelta(Options);

        Assert.True(isValid);
        Assert.Equal(expectedValence, delta.Valence, 10);
        Assert.Equal(expectedArousal, delta.Arousal, 10);
        Assert.Equal("annoyed", delta.Mood);
        Assert.Equal("user insulted me", delta.Reason);
    }

    [Fact]
    public void ToDelta_IgnoresCaseAndWhitespace()
    {
        var (delta, isValid) = new EmotionReaction(" Good_News ", "HIGH", "", "").ToDelta(Options);

        Assert.True(isValid);
        Assert.Equal(0.225, delta.Valence, 10);
        Assert.Equal(0.225, delta.Arousal, 10);
    }

    [Fact]
    public void ToDelta_UnknownEventType_FallsBackToNeutral()
    {
        var (delta, isValid) = new EmotionReaction("ecstatic", "high", "thrilled", "").ToDelta(Options);

        Assert.False(isValid);
        Assert.Equal(0, delta.Valence);
        Assert.Equal(0, delta.Arousal);
        Assert.Equal("thrilled", delta.Mood);
    }

    [Fact]
    public void ToDelta_UnknownIntensity_FallsBackToLow()
    {
        var (delta, isValid) = new EmotionReaction("affection", "extreme", "", "").ToDelta(Options);

        Assert.False(isValid);
        Assert.Equal(0.075, delta.Valence, 10);
        Assert.Equal(0.025, delta.Arousal, 10);
    }

    [Fact]
    public void ToDelta_NullFields_FallBackWithoutThrowing()
    {
        var (delta, isValid) = new EmotionReaction(null!, null!, null!, null!).ToDelta(Options);

        Assert.False(isValid);
        Assert.Equal(0, delta.Valence);
        Assert.Equal(string.Empty, delta.Mood);
        Assert.Equal(string.Empty, delta.Reason);
    }

    [Fact]
    public void ToDelta_DoesNotApplyThePerTurnCap()
    {
        var options = new EmotionOptions();
        options.Events["rude"] = new EmotionEventEffect { Valence = -2, Arousal = 0 };

        var (delta, _) = new EmotionReaction("rude", "medium", "", "").ToDelta(options);

        // The cap belongs to EmotionService.ApplyAsync.
        Assert.Equal(-2, delta.Valence);
    }
}
