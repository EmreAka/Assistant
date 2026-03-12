using Assistant.Api.Services.Abstracts;
using Microsoft.Agents.AI;

namespace Assistant.Api.Services.Concretes;

public class MemoryContextProvider(
    long chatId,
    string userInput,
    IMemoryService memoryService
) : AIContextProvider
{
    private const int MaxMemories = 15;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var memories = await memoryService.SearchRelevantMemoriesAsync(chatId, userInput, MaxMemories, cancellationToken);
        if (memories.Count == 0)
        {
            return new AIContext();
        }

        await memoryService.TouchMemoriesAsync(memories.Select(x => x.Id), cancellationToken);

        var memoryLines = memories
            .Select(x => $"- [{x.Category}] {x.Content}")
            .ToArray();

        return new AIContext
        {
            Instructions = $$"""
                             Known User Memories:
                             {{string.Join(Environment.NewLine, memoryLines)}}
                             """
        };
    }
}
