using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Tests.ChatFeatures;

public class AssistantTimeServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 4, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GetLocalNow_UsesConfiguredDefaultTimeZone()
    {
        var service = CreateService();
        var expected = TimeZoneInfo.ConvertTimeFromUtc(FixedUtcNow.UtcDateTime, service.DefaultTimeZone);

        var result = service.GetLocalNow();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertLocalToUtc_RoundTripsConfiguredDefaultTimeZone()
    {
        var service = CreateService();
        var localTime = service.GetLocalNow();

        var result = service.ConvertLocalToUtc(localTime);

        Assert.Equal(FixedUtcNow.UtcDateTime, result);
    }

    [Fact]
    public void ConvertUtcToLocal_NormalizesAllDateTimeKinds()
    {
        var service = CreateService();
        var utcValue = FixedUtcNow.UtcDateTime;
        var localValue = utcValue.ToLocalTime();
        var unspecifiedValue = DateTime.SpecifyKind(utcValue, DateTimeKind.Unspecified);
        var expected = TimeZoneInfo.ConvertTimeFromUtc(utcValue, service.DefaultTimeZone);

        Assert.Equal(expected, service.ConvertUtcToLocal(utcValue));
        Assert.Equal(expected, service.ConvertUtcToLocal(localValue));
        Assert.Equal(expected, service.ConvertUtcToLocal(unspecifiedValue));
    }

    [Fact]
    public void FormatUtcForDisplay_FallsBackToUtc_WhenTimeZoneIsInvalid()
    {
        var service = CreateService();

        var result = service.FormatUtcForDisplay(FixedUtcNow.UtcDateTime, "Invalid/TimeZone", "yyyy-MM-dd HH:mm:ss");

        Assert.Equal("2026-04-18 09:30:00 UTC", result);
    }

    private static AssistantTimeService CreateService()
    {
        return new AssistantTimeService(
            Options.Create(new AiProvidersOptions
            {
                DefaultTimeZoneId = "Europe/Istanbul"
            }),
            new FixedTimeProvider(FixedUtcNow));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
