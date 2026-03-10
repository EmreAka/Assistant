using Pgvector;

namespace Assistant.Api.Domain.Entities;

public class UserMemory
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Importance { get; set; }
    public Vector? Embedding { get; set; }
    public string Status { get; set; } = UserMemoryStatuses.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public int? MergedIntoMemoryId { get; set; }
    public UserMemory? MergedIntoMemory { get; set; }
    public ICollection<UserMemory> MergedMemories { get; set; } = new List<UserMemory>();
    public DateTime? LastConsolidatedAt { get; set; }
}
