using Assistant.Api.Domain.Dtos;

namespace Assistant.Api.Services.Abstracts;

public interface IReminderSchedulerService
{
    Task<ReminderToolResponse> CreateReminderAsync(
        long chatId,
        string reminderText,
        bool isRecurring,
        string? cronExpression,
        string? runAtLocalIso,
        string? timeZoneId,
        CancellationToken cancellationToken
    );
}
