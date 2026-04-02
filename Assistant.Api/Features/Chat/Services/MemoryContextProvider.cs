using System.Globalization;
using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.Agents.AI;

namespace Assistant.Api.Features.Chat.Services;

public class MemoryContextProvider(
    long chatId,
    IMemoryService memoryService,
    string defaultTimeZoneId
) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var activeMemories = await memoryService.GetActiveMemoriesAsync(chatId, cancellationToken);
        var expiredTimeBoundMemories = await memoryService.GetRecentExpiredTimeBoundMemoriesAsync(chatId, cancellationToken);

        if (activeMemories.Count == 0 && expiredTimeBoundMemories.Count == 0)
        {
            return new AIContext();
        }

        if (activeMemories.Count > 0)
        {
            await memoryService.TouchMemoriesAsync(activeMemories.Select(x => x.Id), cancellationToken);
        }

        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(defaultTimeZoneId);
        var sections = new List<string>();

        if (activeMemories.Count > 0)
        {
            var activeMemoryLines = activeMemories
                .Select(x => FormatMemoryLine(x, timeZoneInfo))
                .ToArray();

            sections.Add(
                $$"""
                  Current memories:
                  {{string.Join(Environment.NewLine, activeMemoryLines)}}
                  """);
        }

        if (expiredTimeBoundMemories.Count > 0)
        {
            var expiredTimeBoundMemoryLines = expiredTimeBoundMemories
                .Select(x => FormatMemoryLine(x, timeZoneInfo))
                .ToArray();

            sections.Add(
                $$"""
                  Time-bound memories that may no longer be current:
                  {{string.Join(Environment.NewLine, expiredTimeBoundMemoryLines)}}
                  """);
        }

        return new AIContext
        {
            Instructions = $$"""
                             Known User Memories (timestamps are local to {{defaultTimeZoneId}}):
                             {{string.Join(Environment.NewLine + Environment.NewLine, sections)}}
                             """
        };
    }

    private static string FormatMemoryLine(UserMemory memory, TimeZoneInfo timeZoneInfo)
    {
        var createdAtLocal = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(memory.CreatedAt), timeZoneInfo)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var expiresAtLocal = memory.ExpiresAt.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(ToUtc(memory.ExpiresAt.Value), timeZoneInfo)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "none";

        return $"- [{memory.Category}] {memory.Content} (created: {createdAtLocal}; expires: {expiresAtLocal})";
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
