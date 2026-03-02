using Assistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

namespace Assistant.Api.Services.Concretes;

public class WorkdayEndReminderJob(
    ApplicationDbContext dbContext,
    ITelegramBotClient botClient,
    ILogger<WorkdayEndReminderJob> logger)
{
    public async Task ExecuteAsync()
    {
        var chatIds = await dbContext.TelegramUsers
            .AsNoTracking()
            .Select(x => x.ChatId)
            .ToListAsync();

        logger.LogInformation("Workday end reminder job started. User count: {UserCount}", chatIds.Count);

        if (chatIds.Count == 0)
        {
            logger.LogInformation("No registered users found. Workday end reminder job finished with no-op.");
            return;
        }

        var sentCount = 0;
        var failedCount = 0;

        foreach (var chatId in chatIds)
        {
            try
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Hey dostum mesai bitti! Bugün yaptığın işleri gözden geçirmeyi ve yarın için not almayı unutma. Yarın görüşmek üzere!",
                    cancellationToken: CancellationToken.None);

                sentCount++;
            }
            catch (Exception exception)
            {
                failedCount++;
                logger.LogError(exception, "Failed to send workday end reminder to chat {ChatId}", chatId);
            }
        }

        logger.LogInformation(
            "Workday end reminder job finished. Attempted: {AttemptedCount}, Sent: {SentCount}, Failed: {FailedCount}",
            chatIds.Count,
            sentCount,
            failedCount);
    }
}
