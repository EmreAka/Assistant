using System.Text.Json;
using Assistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.Chat.Services;

/// <summary>
/// Keeps serialized <see cref="Microsoft.Agents.AI.AgentSession"/>s in the agent_sessions table so chat history
/// survives app restarts.
/// </summary>
public sealed class AgentSessionStore(ApplicationDbContext dbContext) : IAgentSessionStore
{
    public async Task<JsonElement?> GetAsync(long chatId, CancellationToken cancellationToken = default)
    {
        var json = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => x.Session)
            .FirstOrDefaultAsync(cancellationToken);

        return json is null ? null : JsonElement.Parse(json);
    }

    // One upsert statement instead of a tracked entity: the DbContext is shared with the rest of the scope
    // (DeferredIntentDispatchJob saves its intent after the run), and a failed save must not leave a pending
    // entity behind for their SaveChangesAsync.
    public async Task SaveAsync(long chatId, JsonElement session, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO agent_sessions (chat_id, session, created_at, updated_at)
             VALUES ({chatId}, {session.GetRawText()}::json, now(), now())
             ON CONFLICT (chat_id) DO UPDATE SET session = EXCLUDED.session, updated_at = EXCLUDED.updated_at
             """,
            cancellationToken);
    }
}
