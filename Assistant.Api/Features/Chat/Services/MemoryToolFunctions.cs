using System.ComponentModel;
using Assistant.Api.Features.UserManagement.Services;

namespace Assistant.Api.Features.Chat.Services;

public class MemoryToolFunctions(
    long chatId,
    IMemoryService memoryService,
    ILogger<MemoryToolFunctions> logger
)
{
    [Description("Saves a useful user memory when the user shares a preference, profile detail, recurring behavior, ongoing project, relationship, constraint, or goal that may help in future conversations.")]
    public async Task<string> SaveMemory(
        [Description("The memory content rewritten as a concise standalone fact or short summary. Generalize overly specific one-off details into a broader useful memory when possible.")] string content,
        [Description("The memory category. Prefer one of: preference, profile, goal, fact.")] string category,
        [Description("Memory importance from 1 to 10. When unsure but the memory seems useful later, prefer saving it with a medium-high score like 6 to 8 instead of skipping it.")] int importance)
    {
        var saved = await memoryService.SaveMemoryAsync(chatId, content, category, importance, CancellationToken.None);

        if (saved)
        {
            logger.LogInformation("User memory saved. ChatId: {ChatId}, Category: {Category}", chatId, category);
            return "Memory saved.";
        }

        logger.LogInformation("User memory skipped or refreshed. ChatId: {ChatId}, Category: {Category}", chatId, category);
        return "Memory already exists or was not eligible.";
    }
}
