namespace Assistant.Api.Features.UserManagement.Models;

public class UserMemoryManifest
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public DateTime UpdatedAt { get; set; }
}
