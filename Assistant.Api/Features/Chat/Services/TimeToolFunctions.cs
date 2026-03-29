using System.ComponentModel;
using Assistant.Api.Domain.Configurations;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class TimeToolFunctions(
    IOptions<AiProvidersOptions> aiOptions
)
{
    [Description("Returns the current local date and time. Use this when you need to resolve relative time expressions like 'tomorrow', 'next week', or 'in 2 hours'.")]
    public string GetCurrentDateTime()
    {
        var timeZoneId = aiOptions.Value.DefaultTimeZoneId;
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZoneInfo);

        return $"Current local datetime in {timeZoneId}: {nowLocal:yyyy-MM-dd HH:mm:ss}";
    }
}
