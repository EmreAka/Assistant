using System.Globalization;
using Assistant.Api.Domain.Configurations;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class AssistantTimeService(
    IOptions<AiProvidersOptions> aiOptions,
    TimeProvider timeProvider
) : IAssistantTimeService
{
    public string DefaultTimeZoneId { get; } = aiOptions.Value.DefaultTimeZoneId;

    public TimeZoneInfo DefaultTimeZone { get; } = ResolveDefaultTimeZone(aiOptions.Value.DefaultTimeZoneId);

    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public DateTime GetLocalNow()
    {
        return ConvertUtcToLocal(UtcNow);
    }

    public DateTime ConvertLocalToUtc(DateTime localDateTime, string? timeZoneId = null)
    {
        return TimeZoneInfo.ConvertTimeToUtc(localDateTime, ResolveTimeZone(timeZoneId));
    }

    public DateTime ConvertUtcToLocal(DateTime utcDateTime, string? timeZoneId = null)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(utcDateTime), ResolveTimeZone(timeZoneId));
    }

    public string FormatUtcForDisplay(DateTime utcDateTime, string timeZoneId, string format)
    {
        var normalizedUtc = NormalizeUtc(utcDateTime);

        try
        {
            var localTime = ConvertUtcToLocal(normalizedUtc, timeZoneId);
            return localTime.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (TimeZoneNotFoundException)
        {
            return $"{normalizedUtc.ToString(format, CultureInfo.InvariantCulture)} UTC";
        }
        catch (InvalidTimeZoneException)
        {
            return $"{normalizedUtc.ToString(format, CultureInfo.InvariantCulture)} UTC";
        }
    }

    private static TimeZoneInfo ResolveDefaultTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new InvalidOperationException("AIProviders:DefaultTimeZoneId is not configured.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"AIProviders:DefaultTimeZoneId '{timeZoneId}' was not found on this system.",
                ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new InvalidOperationException(
                $"AIProviders:DefaultTimeZoneId '{timeZoneId}' is invalid on this system.",
                ex);
        }
    }

    private TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        return string.IsNullOrWhiteSpace(timeZoneId)
            ? DefaultTimeZone
            : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
