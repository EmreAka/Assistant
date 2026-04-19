using System.Globalization;
using Assistant.Api.Data;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.Chat.Services;

public class TemporalContextProvider(
    long chatId,
    ApplicationDbContext dbContext,
    IAssistantTimeService assistantTimeService
) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = assistantTimeService.UtcNow;
        var nowLocal = assistantTimeService.GetLocalNow();

        var lastChatActivityAtUtc = await (
                from turn in dbContext.ChatTurns.AsNoTracking()
                join user in dbContext.TelegramUsers.AsNoTracking()
                    on turn.TelegramUserId equals user.Id
                where user.ChatId == chatId
                orderby turn.CreatedAt descending
                select (DateTime?)turn.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new AIContext
        {
            Instructions = BuildTemporalContext(nowUtc, nowLocal, lastChatActivityAtUtc)
        };
    }

    private string BuildTemporalContext(
        DateTime nowUtc,
        DateTime nowLocal,
        DateTime? lastChatActivityAtUtc)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        var thisWeekStart = GetWeekStart(today);
        var thisWeekEnd = thisWeekStart.AddDays(6);
        var nextWeekStart = thisWeekStart.AddDays(7);
        var nextWeekEnd = nextWeekStart.AddDays(6);

        var lastChatActivityLines = BuildLastChatActivityLines(nowUtc, nowLocal, lastChatActivityAtUtc);

        return $"""
                Temporal Context:
                - now_local: {FormatLocalDateTime(nowLocal)} {assistantTimeService.DefaultTimeZoneId}
                - day_of_week: {nowLocal.DayOfWeek}
                - day_period: {GetDayPeriod(nowLocal)}
                - yesterday: {FormatDate(today.AddDays(-1))}
                - today: {FormatDate(today)}
                - tomorrow: {FormatDate(today.AddDays(1))}
                - tonight: {FormatDate(today)} evening/night
                - this_week: {FormatDate(thisWeekStart)}..{FormatDate(thisWeekEnd)}
                - next_week: {FormatDate(nextWeekStart)}..{FormatDate(nextWeekEnd)}
                {lastChatActivityLines}
                """;
    }

    private string BuildLastChatActivityLines(
        DateTime nowUtc,
        DateTime nowLocal,
        DateTime? lastChatActivityAtUtc)
    {
        if (!lastChatActivityAtUtc.HasValue)
        {
            return """
                   - last_chat_activity_local: none
                   - elapsed_since_last_chat_activity: none
                   - conversation_pacing: no_previous_activity
                   """;
        }

        var normalizedLastChatActivityUtc = NormalizeUtc(lastChatActivityAtUtc.Value);
        var elapsed = nowUtc - normalizedLastChatActivityUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var lastChatActivityLocal = assistantTimeService.ConvertUtcToLocal(normalizedLastChatActivityUtc);

        return $"""
                - last_chat_activity_local: {FormatLocalDateTime(lastChatActivityLocal)} {assistantTimeService.DefaultTimeZoneId}
                - elapsed_since_last_chat_activity: {FormatDuration(elapsed)}
                - conversation_pacing: {GetConversationPacing(elapsed, nowLocal, lastChatActivityLocal)}
                """;
    }

    private static DateOnly GetWeekStart(DateOnly value)
    {
        var daysSinceMonday = ((int)value.DayOfWeek + 6) % 7;
        return value.AddDays(-daysSinceMonday);
    }

    private static string GetDayPeriod(DateTime localTime)
    {
        return localTime.Hour switch
        {
            >= 0 and <= 4 => "early_morning",
            >= 5 and <= 11 => "morning",
            >= 12 and <= 16 => "afternoon",
            >= 17 and <= 20 => "evening",
            _ => "night"
        };
    }

    private static string GetConversationPacing(
        TimeSpan elapsed,
        DateTime nowLocal,
        DateTime lastChatActivityLocal)
    {
        if (elapsed < TimeSpan.FromMinutes(2))
        {
            return "immediate";
        }

        if (elapsed < TimeSpan.FromMinutes(30))
        {
            return "short_gap";
        }

        if (DateOnly.FromDateTime(nowLocal) == DateOnly.FromDateTime(lastChatActivityLocal))
        {
            return "same_day_gap";
        }

        return elapsed < TimeSpan.FromDays(2)
            ? "long_gap"
            : "multi_day_gap";
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "less than 1 minute";
        }

        var units = new List<string>(capacity: 2);
        AddDurationUnit(units, elapsed.Days, "day");
        AddDurationUnit(units, elapsed.Hours, "hour");
        AddDurationUnit(units, elapsed.Minutes, "minute");

        return string.Join(" ", units.Take(2));
    }

    private static void AddDurationUnit(List<string> units, int value, string unitName)
    {
        if (value <= 0)
        {
            return;
        }

        units.Add(value == 1 ? $"1 {unitName}" : $"{value} {unitName}s");
    }

    private static string FormatLocalDateTime(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDate(DateOnly value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
