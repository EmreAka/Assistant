using System.ComponentModel;
namespace Assistant.Api.Features.Chat.Services;

public class TimeToolFunctions(
    IAssistantTimeService assistantTimeService
)
{
    [Description("Returns the current local date and time. Use this when you need to resolve relative time expressions like 'tomorrow', 'next week', or 'in 2 hours'.")]
    public string GetCurrentDateTime()
    {
        var timeZoneId = assistantTimeService.DefaultTimeZoneId;
        var nowLocal = assistantTimeService.GetLocalNow();

        return $"Current local datetime in {timeZoneId}: {nowLocal:yyyy-MM-dd HH:mm:ss}";
    }
}
