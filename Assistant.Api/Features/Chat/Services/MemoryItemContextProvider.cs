using Assistant.Api.Features.UserManagement.Services;
using Microsoft.Agents.AI;
using Pgvector;

namespace Assistant.Api.Features.Chat.Services;

// Replaces MemoryContextProvider: core memory items every turn, plus the non-core items relevant
// to the current message.
public class MemoryItemContextProvider(
    long chatId,
    IMemoryItemService memoryItemService,
    Vector? queryVector,
    ILogger logger
) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var coreItems = await memoryItemService.GetCoreItemsAsync(chatId, cancellationToken);
            IReadOnlyList<MemoryItemSearchResult> relevantItems = queryVector is null
                ? []
                : await memoryItemService.SearchAsync(chatId, queryVector, cancellationToken);

            if (coreItems.Count == 0 && relevantItems.Count == 0)
            {
                return new AIContext();
            }

            var sections = new List<string>(capacity: 2);
            if (coreItems.Count > 0)
            {
                sections.Add(FormatSection("Known facts about the user:", coreItems.Select(x => (x.Category, x.Text))));
            }

            if (relevantItems.Count > 0)
            {
                sections.Add(FormatSection("Facts about the user that may be relevant to this message:", relevantItems.Select(x => (x.Category, x.Text))));
            }

            return new AIContext
            {
                Instructions = string.Join(Environment.NewLine + Environment.NewLine, sections)
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Memory must never break a chat reply; continue without it.
            logger.LogWarning(ex, "Memory item context failed; continuing without memory. ChatId: {ChatId}", chatId);
            return new AIContext();
        }
    }

    private static string FormatSection(string heading, IEnumerable<(string Category, string Text)> items)
    {
        return $"""
                {heading}
                {string.Join(Environment.NewLine, items.Select(x => $"- [{x.Category}] {x.Text}"))}
                """;
    }
}
