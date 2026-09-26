using System.Text.Json;

namespace Assistant.Api.Features.Chat.Services;

public interface IAgentSessionStore
{
    /// <summary>Returns the serialized agent session for the chat, or null when none is stored.</summary>
    Task<JsonElement?> GetAsync(long chatId, CancellationToken cancellationToken = default);

    Task SaveAsync(long chatId, JsonElement session, CancellationToken cancellationToken = default);
}
