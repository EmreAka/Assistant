using Assistant.Api.Data;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Features.Chat.Services;

public class DeferredIntentDispatchJob(
    ApplicationDbContext dbContext,
    IAgentService agentService,
    ITelegramBotClient botClient,
    ILogger<DeferredIntentDispatchJob> logger
)
{
    public async Task ExecuteAsync(Guid intentId)
    {
        var intent = await dbContext.DeferredIntents
            .FirstOrDefaultAsync(x => x.IntentId == intentId);

        if (intent == null || intent.Status != DeferredIntentStatuses.Scheduled)
        {
            logger.LogWarning("Deferred intent not found or not in scheduled state: {IntentId}", intentId);
            return;
        }

        try
        {
            logger.LogInformation("Waking up agent for deferred intent: {IntentId}", intentId);

            var augmentation = $"""
                                YOU ARE NOW EXECUTING A DEFERRED TASK.
                                The user asked you to perform this task earlier.
                                ORIGINAL INSTRUCTION: {intent.OriginalInstruction}
                                Current UTC Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}

                                MISSION: Use your personality and tools to complete the goal. 
                                Do not ask the user for permission; just do it and report the result.
                                Reply as if you are continuing the earlier conversation.
                                """;

            var result = await agentService.RunAsync(
                intent.ChatId,
                $"Execute the deferred task: {intent.OriginalInstruction}",
                systemInstructionsAugmentation: augmentation,
                cancellationToken: CancellationToken.None
            );

            await botClient.SendMessage(
                chatId: intent.ChatId,
                text: result,
                parseMode: ParseMode.Markdown,
                cancellationToken: CancellationToken.None
            );

            intent.Status = DeferredIntentStatuses.Completed;
            intent.ExecutionResult = result;
            intent.ExecutedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            
            logger.LogInformation("Deferred intent executed successfully: {IntentId}", intentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute deferred intent: {IntentId}", intentId);
            intent.Status = DeferredIntentStatuses.Failed;
            intent.ExecutionResult = $"Error: {ex.Message}";
            await dbContext.SaveChangesAsync();
        }
    }
}
