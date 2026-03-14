using Assistant.Api.Features.UserManagement.Models;

namespace Assistant.Api.Features.Expense.Models;

public class Expense
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public TelegramUser? TelegramUser { get; set; }
    
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public string Description { get; set; } = string.Empty;
    public DateTime BillingPeriodStartDate { get; set; }
    public DateTime BillingPeriodEndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
