using Assistant.Api.Data;
using Assistant.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

namespace Assistant.Api.Services.Concretes;

public class ReminderDispatchJob(
    ApplicationDbContext dbContext,
    ITelegramBotClient botClient,
    ILogger<ReminderDispatchJob> logger
)
{
    public async Task ExecuteAsync(Guid reminderId)
    {
        var reminder = await dbContext.Reminders
            .FirstOrDefaultAsync(x => x.ReminderId == reminderId);

        if (reminder is null)
        {
            logger.LogWarning("Reminder not found for dispatch. ReminderId: {ReminderId}", reminderId);
            return;
        }

        try
        {
            await botClient.SendMessage(
                chatId: reminder.ChatId,
                text: reminder.Message,
                cancellationToken: CancellationToken.None
            );

            reminder.LastSentAtUtc = DateTime.UtcNow;
            reminder.UpdatedAt = DateTime.UtcNow;
            reminder.LastError = null;
            if (!reminder.IsRecurring)
            {
                reminder.Status = ReminderStatuses.Completed;
            }

            await dbContext.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to dispatch reminder. ReminderId: {ReminderId}, ChatId: {ChatId}",
                reminder.ReminderId,
                reminder.ChatId
            );

            reminder.LastError = exception.Message;
            reminder.UpdatedAt = DateTime.UtcNow;
            if (!reminder.IsRecurring)
            {
                reminder.Status = ReminderStatuses.Failed;
            }

            await dbContext.SaveChangesAsync();
        }
    }
}
