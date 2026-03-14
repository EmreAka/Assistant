namespace Assistant.Api.Features.UserManagement.Models;

public class AssistantPersonality
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public string PersonalityText { get; set; } = string.Empty;
    public TelegramUser TelegramUser { get; set; } = null!;
}
