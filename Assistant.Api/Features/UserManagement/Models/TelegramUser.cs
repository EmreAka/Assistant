using ChatTurnModel = Assistant.Api.Features.Chat.Models.ChatTurn;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.UserManagement.Models;

public class TelegramUser
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public AssistantPersonality? AssistantPersonality { get; set; }
    public ICollection<ChatTurnModel> ChatTurns { get; set; } = new List<ChatTurnModel>();
    public ICollection<ExpenseModel> Expenses { get; set; } = new List<ExpenseModel>();
}
