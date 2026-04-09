using NpgsqlTypes;

namespace Assistant.Api.Features.UserManagement.Models;

public class UserMemory
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Importance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public NpgsqlTsVector SearchVector { get; set; } = null!;
}
