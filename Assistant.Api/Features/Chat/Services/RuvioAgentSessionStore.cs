using System.Text.Json;
using Ruvio.Client;

namespace Assistant.Api.Features.Chat.Services;

/// <summary>
/// Keeps serialized <see cref="Microsoft.Agents.AI.AgentSession"/>s in Ruvio so chat history survives app restarts.
/// </summary>
public sealed class RuvioAgentSessionStore(RuvioClient ruvioClient) : IAgentSessionStore
{
    private const string KeyPrefix = "assistant:agent-session:";

    public async Task<JsonElement?> GetAsync(long chatId, CancellationToken cancellationToken = default)
    {
        var json = await ruvioClient.GetStringAsync(KeyPrefix + chatId, cancellationToken);
        return json is null ? null : JsonElement.Parse(json);
    }

    public Task SaveAsync(long chatId, JsonElement session, CancellationToken cancellationToken = default)
    {
        return ruvioClient.SetAsync(KeyPrefix + chatId, session.GetRawText(), cancellationToken);
    }
}
