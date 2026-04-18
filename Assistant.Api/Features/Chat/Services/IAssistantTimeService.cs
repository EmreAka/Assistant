namespace Assistant.Api.Features.Chat.Services;

public interface IAssistantTimeService
{
    string DefaultTimeZoneId { get; }
    TimeZoneInfo DefaultTimeZone { get; }
    DateTime UtcNow { get; }
    DateTime GetLocalNow();
    DateTime ConvertLocalToUtc(DateTime localDateTime, string? timeZoneId = null);
    DateTime ConvertUtcToLocal(DateTime utcDateTime, string? timeZoneId = null);
    string FormatUtcForDisplay(DateTime utcDateTime, string timeZoneId, string format);
}
