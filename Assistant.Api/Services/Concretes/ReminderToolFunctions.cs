using System.ComponentModel;
using Assistant.Api.Domain.Dtos;
using Assistant.Api.Services.Abstracts;

namespace Assistant.Api.Services.Concretes;

public class ReminderToolFunctions(
    long chatId,
    IReminderSchedulerService reminderSchedulerService,
    ILogger<ReminderToolFunctions> logger
)
{
    public ReminderToolResponse? LastResult { get; private set; }

    [Description("Kullanıcının hatırlatma isteğine göre tek seferlik veya tekrarlayan bir reminder oluşturur.")]
    public async Task<ReminderToolResponse> CreateReminder(
        [Description("Hatırlatma mesajı.")] string reminderText,
        [Description("Hatırlatma tekrarlı mı? true ise tekrarlı, false ise tek seferlik.")] bool isRecurring,
        [Description("Tekrarlı hatırlatma için 5 alanlı cron ifadesi. Tek seferlikte null bırakılmalı.")] string? cronExpression,
        [Description("Tek seferlik hatırlatma için ISO tarih-saat metni (yerel saat). Tekrarlıda null bırakılmalı.")] string? runAtLocalIso,
        [Description("Saat dilimi kimliği. Boş ise varsayılan Europe/Istanbul kullanılır.")] string? timeZoneId
    )
    {
        try
        {
            LastResult = await reminderSchedulerService.CreateReminderAsync(
                chatId,
                reminderText,
                isRecurring,
                cronExpression,
                runAtLocalIso,
                timeZoneId,
                CancellationToken.None
            );
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "CreateReminder tool failed unexpectedly.");
            LastResult = ReminderToolResponse.Invalid("Hatırlatma oluşturulurken beklenmeyen bir hata oluştu.");
        }

        return LastResult;
    }
}
