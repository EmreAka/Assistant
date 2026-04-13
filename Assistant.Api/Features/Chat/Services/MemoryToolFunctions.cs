using System.ComponentModel;
using Assistant.Api.Features.UserManagement.Services;

namespace Assistant.Api.Features.Chat.Services;

public class MemoryToolFunctions(
    long chatId,
    IMemoryService memoryService,
    ILogger<MemoryToolFunctions> logger
)
{
    [Description("Updates the user's memory manifest with the latest information. Provide the complete, full text of the user's updated memory manifesto.")]
    public async Task<string> UpdateMemoryManifest(
        [Description("The full text of the updated memory manifesto.")] string content)
    {
        var saved = await memoryService.SaveManifestAsync(chatId, content, CancellationToken.None);

        if (saved)
        {
            logger.LogInformation("User memory manifest updated. ChatId: {ChatId}", chatId);
            return "Memory manifest updated successfully.";
        }

        return "Failed to update memory manifest.";
    }
}
