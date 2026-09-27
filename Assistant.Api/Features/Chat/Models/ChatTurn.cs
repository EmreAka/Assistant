using Assistant.Api.Features.UserManagement.Models;
using NpgsqlTypes;
using Pgvector;

namespace Assistant.Api.Features.Chat.Models;

public class ChatTurn
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public string UserMessage { get; set; } = string.Empty;
    public string AssistantMessage { get; set; } = string.Empty;
    public NpgsqlTsVector SearchVector { get; set; } = null!;
    public Vector? Embedding { get; set; }
    // NULL means "not extracted into memory items yet", so the column is the extraction work queue.
    public DateTime? MemoryProcessedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
