using Assistant.Api.Data;
using Assistant.Api.Features.Expense.Commands;
using Assistant.Api.Features.Expense.Services;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Tests.Expense;

public class ExpenseCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsSummaryAndDirection_WhenQuestionProvided()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsSummaryAndDirection_WhenQuestionProvided));
        SeedUser(dbContext, 11, 1111);
        dbContext.Expenses.AddRange(
            CreateExpense(11, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 250m, "MARKET"),
            CreateExpense(11, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 75m, "COFFEE"));
        await dbContext.SaveChangesAsync();
        var responseSender = new FakeTelegramResponseSender();
        var command = new ExpenseCommand(
            new FakeExpenseAnalysisService(),
            dbContext,
            responseSender,
            NullLogger<ExpenseCommand>.Instance);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense geçen ay ne harcadım?",
                    Chat = new Chat { Id = 1111 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("Toplam Harcama: 325,00 TRY", responseSender.Messages[0]);
        Assert.Contains("Harcama sorularını normal sohbette sorabilirsin.", responseSender.Messages[0]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSummary_WhenQuestionMissing()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsSummary_WhenQuestionMissing));
        SeedUser(dbContext, 12, 1212);
        dbContext.Expenses.AddRange(
            CreateExpense(12, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 300m, "MARKET"),
            CreateExpense(12, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 100m, "COFFEE"));
        await dbContext.SaveChangesAsync();

        var responseSender = new FakeTelegramResponseSender();
        var command = new ExpenseCommand(
            new FakeExpenseAnalysisService(),
            dbContext,
            responseSender,
            NullLogger<ExpenseCommand>.Instance);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense",
                    Chat = new Chat { Id = 1212 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("Toplam Harcama: 400,00 TRY", responseSender.Messages[0]);
        Assert.Contains("İşlem Sayısı: 2", responseSender.Messages[0]);
        Assert.DoesNotContain("normal sohbette sorabilirsin", responseSender.Messages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsRegistrationMessage_WhenUserMissing()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsRegistrationMessage_WhenUserMissing));
        var responseSender = new FakeTelegramResponseSender();
        var command = new ExpenseCommand(
            new FakeExpenseAnalysisService(),
            dbContext,
            responseSender,
            NullLogger<ExpenseCommand>.Instance);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense bu ay",
                    Chat = new Chat { Id = 9999 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Equal("Önce /start komutu ile kaydolmalısınız.", responseSender.Messages[0]);
    }

    private static ApplicationDbContext CreateDbContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static void SeedUser(ApplicationDbContext dbContext, int userId, long chatId)
    {
        dbContext.TelegramUsers.Add(new TelegramUser
        {
            Id = userId,
            ChatId = chatId,
            CreatedAt = DateTime.UtcNow,
            FirstName = "Test"
        });
        dbContext.SaveChanges();
    }

    private static ExpenseModel CreateExpense(int userId, DateTime expenseDate, decimal amount, string description)
    {
        return new ExpenseModel
        {
            TelegramUserId = userId,
            ExpenseDate = expenseDate,
            Amount = amount,
            Currency = "TRY",
            Description = description,
            StatementFingerprint = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow
        };
    }

    private sealed class FakeExpenseAnalysisService : IExpenseAnalysisService
    {
        public Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ExpenseAnalysisResponse(true, "ok"));
        }
    }

    private sealed class FakeTelegramResponseSender : ITelegramResponseSender
    {
        public List<string> Messages { get; } = [];

        public Task SendResponseAsync(long chatId, string responseText, CancellationToken cancellationToken)
        {
            Messages.Add(responseText);
            return Task.CompletedTask;
        }
    }
}
