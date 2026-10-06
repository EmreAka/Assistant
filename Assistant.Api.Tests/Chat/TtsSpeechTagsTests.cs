using Assistant.Api.Features.Chat.Services;

namespace Assistant.Api.Tests.ChatFeatures;

public class TtsSpeechTagsTests
{
    private const string Original = "Really? That's great! Goodnight, sleep well.";

    [Theory]
    [InlineData("Really? [laugh] That's great! Goodnight, sleep well.")]
    [InlineData("Really? That's great! <slow><soft>Goodnight, sleep well.</soft></slow>")]
    [InlineData("Really? That's great! [sigh] Goodnight, sleep well [pause].")]
    [InlineData(Original)]
    public void IsValidScript_OnlyTagsAdded_IsValid(string tagged)
    {
        Assert.True(TtsSpeechTags.IsValidScript(Original, tagged));
    }

    [Theory]
    // A word changed.
    [InlineData("Really? That's amazing! Goodnight, sleep well.")]
    // Punctuation changed.
    [InlineData("Really! That's great! Goodnight, sleep well.")]
    // Unknown tags.
    [InlineData("Really? [scream] That's great! Goodnight, sleep well.")]
    [InlineData("Really? That's great! <angry>Goodnight, sleep well.</angry>")]
    // Wrapping tags not closed, or closed out of order.
    [InlineData("Really? That's great! <soft>Goodnight, sleep well.")]
    [InlineData("Really? That's great! <slow><soft>Goodnight, sleep well.</slow></soft>")]
    // Inline tag written as a wrapping tag.
    [InlineData("Really? <laugh>That's great!</laugh> Goodnight, sleep well.")]
    public void IsValidScript_ChangedOrBrokenScript_IsInvalid(string tagged)
    {
        Assert.False(TtsSpeechTags.IsValidScript(Original, tagged));
    }

    [Fact]
    public void IsValidScript_TurkishText_IsValid()
    {
        Assert.True(TtsSpeechTags.IsValidScript(
            "Sınavın nasıl geçti? Çok merak ettim!",
            "Sınavın nasıl geçti? [inhale] <higher-pitch>Çok merak ettim!</higher-pitch>"));
    }
}
