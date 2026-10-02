using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;

namespace Assistant.Api.Tests.ChatFeatures;

public class EmotionFollowUpTests
{
    private static readonly EmotionCheckInOptions Options = new();
    private static readonly DateTime LocalNow = new(2026, 10, 3, 14, 0, 0);

    private static DateTime? Resolve(string localTime, EmotionCheckInOptions? options = null) =>
        new EmotionFollowUp(localTime, "how the user's exam went").ResolveRunAtLocal(LocalNow, options ?? Options);

    [Fact]
    public void ResolveRunAtLocal_OutsideQuietHours_KeepsTime()
    {
        Assert.Equal(new DateTime(2026, 10, 4, 12, 30, 0), Resolve("2026-10-04 12:30"));
    }

    [Fact]
    public void ResolveRunAtLocal_LateEvening_MovesToNextMorning()
    {
        Assert.Equal(new DateTime(2026, 10, 4, 9, 0, 0), Resolve("2026-10-03 23:30"));
    }

    [Fact]
    public void ResolveRunAtLocal_EarlyMorning_MovesToEndOfWindowSameDay()
    {
        Assert.Equal(new DateTime(2026, 10, 4, 9, 0, 0), Resolve("2026-10-04 06:15"));
    }

    [Fact]
    public void ResolveRunAtLocal_AtWindowEnd_KeepsTime()
    {
        Assert.Equal(new DateTime(2026, 10, 4, 9, 0, 0), Resolve("2026-10-04 09:00"));
    }

    [Fact]
    public void ResolveRunAtLocal_SameDayWindow_MovesToItsEnd()
    {
        var options = new EmotionCheckInOptions { QuietHoursStart = new TimeOnly(13, 0), QuietHoursEnd = new TimeOnly(15, 0) };

        Assert.Equal(new DateTime(2026, 10, 4, 15, 0, 0), Resolve("2026-10-04 13:45", options));
        Assert.Equal(new DateTime(2026, 10, 4, 16, 0, 0), Resolve("2026-10-04 16:00", options));
    }

    [Fact]
    public void ResolveRunAtLocal_EmptyWindow_KeepsTime()
    {
        var options = new EmotionCheckInOptions { QuietHoursStart = new TimeOnly(0, 0), QuietHoursEnd = new TimeOnly(0, 0) };

        Assert.Equal(new DateTime(2026, 10, 4, 3, 0, 0), Resolve("2026-10-04 03:00", options));
    }

    [Theory]
    [InlineData("2026-10-03 13:00")] // past
    [InlineData("2026-10-03 14:00")] // now
    [InlineData("2026-10-11 12:00")] // beyond MaxDaysAhead
    [InlineData("tomorrow at 10")]
    [InlineData("2026-10-04T12:00:00")]
    [InlineData("")]
    public void ResolveRunAtLocal_InvalidTime_ReturnsNull(string localTime)
    {
        Assert.Null(Resolve(localTime));
    }

    [Fact]
    public void ResolveRunAtLocal_EmptyNote_ReturnsNull()
    {
        Assert.Null(new EmotionFollowUp("2026-10-04 12:30", " ").ResolveRunAtLocal(LocalNow, Options));
    }
}
