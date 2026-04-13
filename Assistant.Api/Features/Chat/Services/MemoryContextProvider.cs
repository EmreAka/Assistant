using Assistant.Api.Features.UserManagement.Services;
using Microsoft.Agents.AI;

namespace Assistant.Api.Features.Chat.Services;

public class MemoryContextProvider(
    long chatId,
    IMemoryService memoryService
) : AIContextProvider
{    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var manifest = await memoryService.GetActiveManifestAsync(chatId, cancellationToken);

        if (string.IsNullOrWhiteSpace(manifest))
        {
            return new AIContext();
        }

        return new AIContext
        {
            Instructions = $$"""
                             User Knowledge Manifesto:
                             {{manifest}}
                             """
        };
    }
}
