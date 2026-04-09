using System.Globalization;
using System.ComponentModel;
using Assistant.Api.Features.UserManagement.Services;

namespace Assistant.Api.Features.Chat.Services;

public class MemoryToolFunctions(
    long chatId,
    string defaultTimeZoneId,
    IMemoryService memoryService,
    ILogger<MemoryToolFunctions> logger
)
{
    [Description("Saves a useful user memory when the user shares a preference, profile detail, recurring behavior, ongoing project, relationship, constraint, or goal that may help in future conversations. For temporary or time-bound details, set expiresAtLocalIso so the memory stops being treated as current later.")]
    public async Task<string> SaveMemory(
        [Description("The memory content rewritten as a concise standalone fact or short summary. Generalize overly specific one-off details into a broader useful memory when possible.")] string content,
        [Description("The memory category. Prefer one of: preference, profile, goal, fact.")] string category,
        [Description("Memory importance from 1 to 10. When unsure but the memory seems useful later, prefer saving it with a medium-high score like 6 to 8 instead of skipping it.")] int importance,
        [Description("Optional local expiration date/time in ISO 8601 format such as 2026-04-05T18:00:00. Use this for trips, temporary plans, short-lived constraints, or other time-bound memories. Leave null for durable memories.")] string? expiresAtLocalIso = null)
    {
        DateTime? expiresAtUtc = null;
        if (!string.IsNullOrWhiteSpace(expiresAtLocalIso))
        {
            if (!DateTime.TryParse(expiresAtLocalIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localExpiration))
            {
                return "Error: Invalid expiresAtLocalIso format. Use ISO 8601 local datetime.";
            }

            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(defaultTimeZoneId);
            expiresAtUtc = TimeZoneInfo.ConvertTimeToUtc(localExpiration, timeZoneInfo);
        }

        var saved = await memoryService.SaveMemoryAsync(chatId, content, category, importance, expiresAtUtc, CancellationToken.None);

        if (saved)
        {
            logger.LogInformation("User memory saved. ChatId: {ChatId}, Category: {Category}", chatId, category);
            return "Memory saved.";
        }

        logger.LogInformation("User memory skipped or refreshed. ChatId: {ChatId}, Category: {Category}", chatId, category);
        return "Memory already exists or was not eligible.";
    }

    [Description("Lists remembered user information. Use this when the user asks what you remember, or before updating/deleting a memory when you need a Memory ID.")]
    public async Task<string> ListMemories(
        [Description("Memory filter. Use active, expired, or all.")] string statusFilter = "active",
        [Description("Optional search text to narrow memories by topic, such as 'coffee', 'project', or 'travel'.")] string? searchText = null,
        [Description("Maximum number of memories to return.")] int limit = 10)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 20);
        var memories = await memoryService.ListMemoriesAsync(chatId, statusFilter, searchText, normalizedLimit, CancellationToken.None);

        if (memories.Count == 0)
        {
            return $"No memories found for filter '{NormalizeStatusFilter(statusFilter)}'.";
        }

        return $$"""
                 Memories for current chat:
                 {{string.Join(Environment.NewLine, memories.Select(FormatMemoryLine))}}
                 """;
    }

    [Description("Updates an existing memory using its Memory ID. Use this when the user corrects, changes, or refines something you previously remembered.")]
    public async Task<string> UpdateMemory(
        [Description("The Memory ID to update. Use the exact numeric Memory ID from ListMemories or memory context.")] int memoryId,
        [Description("Optional new memory content rewritten as a concise standalone fact. Leave null to keep the current content.")] string? content = null,
        [Description("Optional new memory category. Leave null to keep the current category.")] string? category = null,
        [Description("Optional new importance from 1 to 10. Leave null to keep the current importance.")] int? importance = null,
        [Description("Optional new local expiration date/time in ISO 8601 format. Leave null to keep the current expiration.")] string? expiresAtLocalIso = null,
        [Description("Set true to remove any existing expiration from the memory.")] bool clearExpiration = false)
    {
        if (memoryId <= 0)
        {
            return "Error: memoryId must be a positive integer.";
        }

        if (content is null && category is null && importance is null && expiresAtLocalIso is null && !clearExpiration)
        {
            return "Error: Provide at least one field to update.";
        }

        if (clearExpiration && !string.IsNullOrWhiteSpace(expiresAtLocalIso))
        {
            return "Error: Use either expiresAtLocalIso or clearExpiration, not both.";
        }

        if (content is not null && string.IsNullOrWhiteSpace(content))
        {
            return "Error: content cannot be empty when provided.";
        }

        DateTime? expiresAtUtc = null;
        if (!string.IsNullOrWhiteSpace(expiresAtLocalIso))
        {
            if (!DateTime.TryParse(expiresAtLocalIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localExpiration))
            {
                return "Error: Invalid expiresAtLocalIso format. Use ISO 8601 local datetime.";
            }

            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(defaultTimeZoneId);
            expiresAtUtc = TimeZoneInfo.ConvertTimeToUtc(localExpiration, timeZoneInfo);
        }

        var updatedMemory = await memoryService.UpdateMemoryAsync(
            chatId,
            memoryId,
            content,
            category,
            importance,
            expiresAtUtc,
            clearExpiration,
            CancellationToken.None);

        if (updatedMemory is null)
        {
            return "Error: Memory not found for this chat or update content was invalid.";
        }

        logger.LogInformation("User memory updated. ChatId: {ChatId}, MemoryId: {MemoryId}", chatId, memoryId);
        return $"Memory updated successfully. Memory ID: {updatedMemory.Id}";
    }

    [Description("Deletes an existing memory using its Memory ID. Use this when the user explicitly asks you to forget or remove remembered information.")]
    public async Task<string> DeleteMemory(
        [Description("The Memory ID to delete. Use the exact numeric Memory ID from ListMemories or memory context.")] int memoryId)
    {
        if (memoryId <= 0)
        {
            return "Error: memoryId must be a positive integer.";
        }

        var deleted = await memoryService.DeleteMemoryAsync(chatId, memoryId, CancellationToken.None);
        if (!deleted)
        {
            return "Error: Memory not found for this chat.";
        }

        logger.LogInformation("User memory deleted. ChatId: {ChatId}, MemoryId: {MemoryId}", chatId, memoryId);
        return $"Memory deleted successfully. Memory ID: {memoryId}";
    }

    private static string NormalizeStatusFilter(string? statusFilter)
    {
        var normalized = statusFilter?.Trim().ToLowerInvariant();
        return normalized is "active" or "expired" or "all"
            ? normalized
            : "active";
    }

    private static string FormatMemoryLine(UserManagement.Models.UserMemory memory)
    {
        var nowUtc = DateTime.UtcNow;
        var status = memory.ExpiresAt.HasValue && memory.ExpiresAt.Value <= nowUtc
            ? "expired"
            : "active";
        var expires = memory.ExpiresAt.HasValue
            ? memory.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "none";

        return $"- Memory ID: {memory.Id} | Status: {status} | Category: {memory.Category} | Importance: {memory.Importance} | Expires: {expires} | Content: {memory.Content}";
    }
}
