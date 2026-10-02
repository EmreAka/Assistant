using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Features.Chat.Services;

public class DeferredIntentDispatchJob(
    ApplicationDbContext dbContext,
    IAgentService agentService,
    ITelegramBotClient botClient,
    IOptions<EmotionOptions> emotionOptions,
    ILogger<DeferredIntentDispatchJob> logger
)
{
    public async Task ExecuteAsync(Guid intentId)
    {
        var intent = await dbContext.DeferredIntents
            .FirstOrDefaultAsync(x => x.IntentId == intentId);

        if (intent == null || (intent.Status != DeferredIntentStatuses.Scheduled && intent.Status != DeferredIntentStatuses.Recurring))
        {
            logger.LogWarning("Deferred intent not found or not in executable state: {IntentId}", intentId);
            return;
        }

        if (intent.Origin == DeferredIntentOrigins.Self)
        {
            var skipReason = await GetCheckInSkipReasonAsync(intent);
            if (skipReason is not null)
            {
                logger.LogInformation("Self check-in skipped: {IntentId}. {SkipReason}", intentId, skipReason);
                intent.Status = DeferredIntentStatuses.Cancelled;
                intent.ExecutionResult = skipReason;
                intent.ExecutedAtUtc = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
                return;
            }
        }

        try
        {
            logger.LogInformation("Waking up agent for deferred intent: {IntentId}", intentId);

            var augmentation = intent.Origin == DeferredIntentOrigins.Self
                ? $"""
                   YOU ARE NOW CHECKING IN ON THE USER.
                   Earlier in the conversation you decided to check in with them later.
                   CHECK IN ABOUT: {intent.OriginalInstruction}
                   Current UTC Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}

                   MISSION: Send one short, natural message as a friend would, asking how it went.
                   Do not mention scheduling, reminders, or that this was a task.
                   Reply as if you are continuing the earlier conversation.
                   """
                : $"""
                   YOU ARE NOW EXECUTING A DEFERRED TASK.
                   The user asked you to perform this task earlier.
                   ORIGINAL INSTRUCTION: {intent.OriginalInstruction}
                   Current UTC Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
                   Is Recurring: {intent.IsRecurring}

                   MISSION: Use your personality and tools to complete the goal. 
                   Do not ask the user for permission; just do it and report the result.
                   Reply as if you are continuing the earlier conversation.
                   """;

            var result = await agentService.RunAsync(
                intent.ChatId,
                intent.Origin == DeferredIntentOrigins.Self
                    ? intent.OriginalInstruction
                    : $"Execute the deferred task: {intent.OriginalInstruction}",
                systemInstructionsAugmentation: augmentation,
                cancellationToken: CancellationToken.None
            );

            await botClient.SendMessage(
                chatId: intent.ChatId,
                text: result,
                parseMode: ParseMode.Markdown,
                cancellationToken: CancellationToken.None
            );

            if (!intent.IsRecurring)
            {
                intent.Status = DeferredIntentStatuses.Completed;
            }
            
            intent.ExecutionResult = result;
            intent.ExecutedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            
            logger.LogInformation("Deferred intent executed successfully: {IntentId}", intentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute deferred intent: {IntentId}", intentId);
            if (!intent.IsRecurring)
            {
                intent.Status = DeferredIntentStatuses.Failed;
            }
            intent.ExecutionResult = $"Error: {ex.Message}";
            await dbContext.SaveChangesAsync();
        }
    }

    // A check-in the assistant scheduled itself is dropped when check-ins were switched off since, or
    // when the user is already talking: asking "how did it go?" mid-conversation reads as robotic.
    private async Task<string?> GetCheckInSkipReasonAsync(DeferredIntent intent)
    {
        var options = emotionOptions.Value;
        if (!options.Enabled || !options.CheckIns.Enabled)
        {
            return "Self check-in skipped: check-ins are disabled.";
        }

        var lastMessageAtUtc = await dbContext.ChatTurns
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == intent.ChatId)
            .MaxAsync(x => (DateTime?)x.CreatedAt);

        var minGap = TimeSpan.FromMinutes(options.CheckIns.MinMinutesSinceLastMessage);
        if (lastMessageAtUtc is not null && DateTime.UtcNow - lastMessageAtUtc.Value < minGap)
        {
            return $"Self check-in skipped: the user wrote {(int)(DateTime.UtcNow - lastMessageAtUtc.Value).TotalMinutes} minutes ago.";
        }

        return null;
    }
}
