namespace Assistant.Api.Features.Chat.Models;

// A chat's Microsoft.Agents.AI AgentSession (short-term chat history), serialized with SerializeSessionAsync.
public class StoredAgentSession
{
    public long ChatId { get; set; }
    public string Session { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
