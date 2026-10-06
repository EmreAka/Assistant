using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;

namespace Assistant.Api.Tests.ChatFeatures;

public class EmotionDecayTests
{
    private static readonly EmotionOptions Options = new();
    private static readonly DateTime NowUtc = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static AgentEmotionState Stored(double valence, double arousal, DateTime updatedAt) => new()
    {
        TelegramUserId = 1,
        Valence = valence,
        Arousal = arousal,
        Mood = "heartbroken for you",
        Reason = "user's grandma passed away",
        UpdatedAt = updatedAt,
        LastTurnId = 42
    };

    [Fact]
    public void Decay_OneHalfLife_HalvesDistanceToBaseline()
    {
        var decayed = EmotionService.Decay(Stored(-0.5, 0.9, NowUtc.AddHours(-6)), Options, NowUtc);

        Assert.Equal(-0.1, decayed.Valence, 10);
        Assert.Equal(0.7, decayed.Arousal, 10);
        // Exactly half faded is not stale yet.
        Assert.Equal("heartbroken for you", decayed.Mood);
        Assert.Equal("user's grandma passed away", decayed.Reason);
        Assert.Equal(42, decayed.LastTurnId);
    }

    [Fact]
    public void Decay_FutureTimestamp_KeepsStoredMood()
    {
        var decayed = EmotionService.Decay(Stored(-0.5, 0.9, NowUtc.AddHours(1)), Options, NowUtc);

        Assert.Equal(-0.5, decayed.Valence, 10);
        Assert.Equal(0.9, decayed.Arousal, 10);
        Assert.Equal("heartbroken for you", decayed.Mood);
    }

    [Fact]
    public void Decay_NonPositiveHalfLife_TurnsDecayOff()
    {
        var options = new EmotionOptions { HalfLifeHours = 0 };

        var decayed = EmotionService.Decay(Stored(-0.5, 0.9, NowUtc.AddDays(-3)), options, NowUtc);

        Assert.Equal(-0.5, decayed.Valence, 10);
        Assert.Equal("heartbroken for you", decayed.Mood);
    }

    [Fact]
    public void Decay_Stale_DerivesLabelAndDropsReason()
    {
        // Two half-lives: a quarter of the distance is left, valence -0.1, arousal 0.6.
        var decayed = EmotionService.Decay(Stored(-1.3, 0.9, NowUtc.AddHours(-12)), Options, NowUtc);

        Assert.Equal(-0.1, decayed.Valence, 10);
        Assert.Equal(0.6, decayed.Arousal, 10);
        Assert.Equal("tense", decayed.Mood);
        Assert.Equal(string.Empty, decayed.Reason);
        Assert.Equal(42, decayed.LastTurnId);
    }

    [Theory]
    [InlineData(1.0, 1.0, "cheerful")]
    [InlineData(1.0, 0.0, "content")]
    [InlineData(-1.0, 1.0, "tense")]
    [InlineData(-1.0, 0.0, "a bit down")]
    [InlineData(0.5, 0.7, "relaxed")]
    public void Decay_Stale_UsesQuadrantAroundBaseline(double valence, double arousal, string expectedMood)
    {
        // Two half-lives: a quarter of the distance is left.
        var decayed = EmotionService.Decay(Stored(valence, arousal, NowUtc.AddHours(-12)), Options, NowUtc);

        Assert.Equal(expectedMood, decayed.Mood);
    }

    [Theory]
    [InlineData(0.35, 0.45, MoodQuadrant.Baseline)]
    [InlineData(0.6, 0.8, MoodQuadrant.Cheerful)]
    [InlineData(0.6, 0.2, MoodQuadrant.Content)]
    [InlineData(-0.4, 0.8, MoodQuadrant.Tense)]
    [InlineData(-0.4, 0.2, MoodQuadrant.Down)]
    // Only one axis outside the baseline range is enough to leave it.
    [InlineData(0.3, 0.75, MoodQuadrant.Cheerful)]
    public void GetQuadrant_SplitsAroundBaseline(double valence, double arousal, MoodQuadrant expected)
    {
        Assert.Equal(expected, EmotionService.GetQuadrant(valence, arousal, Options));
    }
}
