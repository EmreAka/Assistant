using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Domain.Dtos;
using Assistant.Api.Domain.Entities;
using Assistant.Api.Services.Abstracts;
using Cronos;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Services.Concretes;

public partial class ReminderSchedulerService(
    ApplicationDbContext dbContext,
    IBackgroundJobClient backgroundJobClient,
    IRecurringJobManager recurringJobManager,
    IOptions<AiOptions> aiOptions,
    ILogger<ReminderSchedulerService> logger
) : IReminderSchedulerService
{
    public async Task<ReminderToolResponse> CreateReminderAsync(
        long chatId,
        string reminderText,
        bool isRecurring,
        string? cronExpression,
        string? runAtLocalIso,
        string? timeZoneId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(reminderText))
        {
            return ReminderToolResponse.Invalid("Hatırlatma metni boş olamaz.");
        }

        var configuredTimeZoneId = string.IsNullOrWhiteSpace(timeZoneId)
            ? aiOptions.Value.DefaultTimeZoneId
            : timeZoneId.Trim();

        if (!TryResolveTimeZone(configuredTimeZoneId, out var timeZoneInfo))
        {
            return ReminderToolResponse.Invalid($"Geçersiz saat dilimi: {configuredTimeZoneId}");
        }

        var reminder = new Reminder
        {
            ReminderId = Guid.NewGuid(),
            ChatId = chatId,
            TopicId = null,
            Message = reminderText.Trim(),
            IsRecurring = isRecurring,
            TimeZoneId = timeZoneInfo.Id,
            Status = ReminderStatuses.PendingSchedule,
            HangfireJobId = string.Empty,
            CreatedAt = DateTime.UtcNow
        };

        if (isRecurring)
        {
            if (string.IsNullOrWhiteSpace(cronExpression))
            {
                return ReminderToolResponse.Invalid("Tekrarlayan hatırlatma için cron ifadesi zorunludur.");
            }

            var normalizedCron = cronExpression.Trim();
            if (!IsValidCron(normalizedCron))
            {
                return ReminderToolResponse.Invalid("Geçersiz cron ifadesi. 5 alanlı bir cron bekleniyor.");
            }

            reminder.CronExpression = normalizedCron;
            reminder.RunAtUtc = null;
        }
        else
        {
            if (!TryParseRunAtUtc(runAtLocalIso, timeZoneInfo, out var runAtUtc))
            {
                return ReminderToolResponse.Invalid("Tek seferlik hatırlatma için geçerli bir tarih/saat üretilemedi.");
            }

            if (runAtUtc <= DateTime.UtcNow)
            {
                return ReminderToolResponse.Invalid("Geçmiş bir zamana hatırlatma kurulamaz.");
            }

            reminder.RunAtUtc = runAtUtc;
            reminder.CronExpression = null;
        }

        await dbContext.Reminders.AddAsync(reminder, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            string hangfireJobId;
            if (reminder.IsRecurring)
            {
                hangfireJobId = BuildRecurringJobId(reminder.ChatId, reminder.ReminderId);

                recurringJobManager.AddOrUpdate<ReminderDispatchJob>(
                    hangfireJobId,
                    job => job.ExecuteAsync(reminder.ReminderId),
                    reminder.CronExpression!,
                    new RecurringJobOptions
                    {
                        TimeZone = timeZoneInfo
                    }
                );
            }
            else
            {
                var delay = reminder.RunAtUtc!.Value - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    reminder.Status = ReminderStatuses.Failed;
                    reminder.LastError = "Çalıştırma zamanı geçmişte kaldı.";
                    reminder.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);

                    return ReminderToolResponse.Invalid("Geçmiş bir zamana hatırlatma kurulamaz.");
                }

                hangfireJobId = backgroundJobClient.Schedule<ReminderDispatchJob>(
                    job => job.ExecuteAsync(reminder.ReminderId),
                    delay
                );
            }

            reminder.HangfireJobId = hangfireJobId;
            reminder.Status = ReminderStatuses.Scheduled;
            reminder.UpdatedAt = DateTime.UtcNow;
            reminder.LastError = null;

            await dbContext.SaveChangesAsync(cancellationToken);

            var summary = BuildSummary(reminder, timeZoneInfo);
            return ReminderToolResponse.Created(reminder.ReminderId, summary);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reminder scheduling failed. ReminderId: {ReminderId}", reminder.ReminderId);

            reminder.Status = ReminderStatuses.Failed;
            reminder.LastError = exception.Message;
            reminder.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            return ReminderToolResponse.Invalid("Hatırlatma oluşturulamadı. Lütfen daha net bir zaman ifadesiyle tekrar dene.");
        }
    }

    private static string BuildRecurringJobId(long chatId, Guid reminderId)
    {
        return $"reminder:{chatId}:{reminderId:D}";
    }

    private static string BuildSummary(Reminder reminder, TimeZoneInfo timeZoneInfo)
    {
        if (reminder.IsRecurring)
        {
            return $"Hatırlatma oluşturuldu. Tekrarlı çalışma: `{reminder.CronExpression}` ({timeZoneInfo.Id}).";
        }

        var localRunAt = TimeZoneInfo.ConvertTimeFromUtc(reminder.RunAtUtc!.Value, timeZoneInfo);
        return $"Hatırlatma oluşturuldu. Zaman: {localRunAt:yyyy-MM-dd HH:mm} ({timeZoneInfo.Id}).";
    }

    private static bool IsValidCron(string cronExpression)
    {
        try
        {
            var expression = CronExpression.Parse(cronExpression, CronFormat.Standard);
            return expression.GetNextOccurrence(DateTime.UtcNow) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseRunAtUtc(
        string? runAtLocalIso,
        TimeZoneInfo timeZoneInfo,
        out DateTime runAtUtc
    )
    {
        runAtUtc = default;
        if (string.IsNullOrWhiteSpace(runAtLocalIso))
        {
            return false;
        }

        var input = runAtLocalIso.Trim();

        if (HasOffsetSuffixRegex().IsMatch(input) &&
            DateTimeOffset.TryParse(
                input,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedOffset
            ))
        {
            runAtUtc = parsedOffset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParse(
                input,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsedLocalDateTime
            ))
        {
            return false;
        }

        var localUnspecified = DateTime.SpecifyKind(parsedLocalDateTime, DateTimeKind.Unspecified);
        try
        {
            runAtUtc = TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timeZoneInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo timeZoneInfo)
    {
        try
        {
            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback for Windows hosted deployments.
            if (string.Equals(timeZoneId, "Europe/Istanbul", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
                    return true;
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (InvalidTimeZoneException)
        {
            // ignored
        }

        timeZoneInfo = TimeZoneInfo.Utc;
        return false;
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffsetSuffixRegex();
}
