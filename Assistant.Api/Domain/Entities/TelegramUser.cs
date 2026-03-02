namespace Assistant.Api.Domain.Entities;

public class TelegramUser
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}
