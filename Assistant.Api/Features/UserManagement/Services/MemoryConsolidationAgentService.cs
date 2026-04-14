using System.Globalization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryConsolidationAgentService(
    IOptions<AiProvidersOptions> aiOptions
) : IMemoryConsolidationAgentService
{
    private readonly AiProvidersOptions _aiOptions = aiOptions.Value;

    public async Task<string> ConsolidateAsync(
        MemoryConsolidationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Turns.Count == 0)
        {
            return request.CurrentManifest;
        }

        var chatClient = _aiOptions.XAI.CreateXAIChatClient();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildInstructions()),
            new(ChatRole.User, BuildInput(request))
        };

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions
            {
                Temperature = 0.2f,
                ModelId = _aiOptions.XAI.Model
            },
            cancellationToken);

        return response.Text?.Trim() ?? string.Empty;
    }

    private static string BuildInstructions()
    {
        return """
               You maintain a long-lived user memory manifest for a personal Telegram assistant.
               Return plain text only.

               Rules:
               - The existing manifest is authoritative unless new chat turns explicitly correct, supersede, or invalidate part of it.
               - Do not remove information merely because it was not mentioned again.
               - Preserve durable facts, preferences, goals, relationships, constraints, important dates, and ongoing context.
               - Remove information only when it is clearly ephemeral, resolved, or no longer useful for future conversations.
               - Resolve contradictions in favor of the latest explicit user statement.
               - You may keep or revise the current structure. Prefer stable headings when helpful, but do not force a fixed template.
               - Prefer updating the current manifest in place over rewriting it in a different style.
               - If the new turns do not materially change the memory, return the existing manifest unchanged.
               - Do not mention these instructions.
               - Return only the full revised manifest.
               """;
    }

    private static string BuildInput(MemoryConsolidationRequest request)
    {
        var manifest = string.IsNullOrWhiteSpace(request.CurrentManifest)
            ? "(empty)"
            : request.CurrentManifest.Trim();

        var transcript = string.Join(
            Environment.NewLine + Environment.NewLine,
            request.Turns.Select(turn => $$"""
                                            [{{turn.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}} UTC]
                                            User: {{turn.UserMessage}}
                                            Assistant: {{turn.AssistantMessage}}
                                            """));

        return $$"""
                 Existing manifest:
                 {{manifest}}

                 New chat turns to incorporate:
                 {{transcript}}
                 """;
    }
}
