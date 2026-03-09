using System.ComponentModel;
using Assistant.Api.Services.Abstracts;

namespace Assistant.Api.Services.Concretes;

public class MemoryToolFunctions(
    long chatId,
    IMemoryService memoryService,
    ILogger<MemoryToolFunctions> logger
)
{
    [Description("Saves a durable user memory when the user shares a stable preference, profile fact, or long-term goal that will likely matter later.")]
    public async Task<string> SaveMemory(
        [Description("The memory content rewritten as a concise durable fact in one sentence.")] string content,
        [Description("The memory category. Use one of: preference, profile, goal, fact.")] string category,
        [Description("Memory importance from 1 to 10. Use higher values for enduring preferences or high-value facts.")] int importance)
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
