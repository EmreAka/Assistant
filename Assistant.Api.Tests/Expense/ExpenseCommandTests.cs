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
            CreateExpense(11, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 250m, "MARKET", "stmt-2", "Shopping"),
            CreateExpense(11, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 75m, "COFFEE", "stmt-2", "Food & Dining"),
            CreateExpense(11, new DateTime(2026, 2, 16, 0, 0, 0, DateTimeKind.Utc), 50m, "OLD", "stmt-1", "Other"));
        await dbContext.SaveChangesAsync();
        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

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
        Assert.Contains("Toplam Harcama: 375,00 TRY", responseSender.Messages[0]);
        Assert.Contains("*Ekstre Dönemleri*", responseSender.Messages[0]);
        Assert.Contains("*Kategoriler*", responseSender.Messages[0]);
        Assert.Contains("Harcama sorularını normal sohbette sorabilirsin.", responseSender.Messages[0]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSummaryWithStatementPeriods_WhenQuestionMissing()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsSummaryWithStatementPeriods_WhenQuestionMissing));
        SeedUser(dbContext, 12, 1212);
        dbContext.Expenses.AddRange(
            CreateExpense(12, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 300m, "MARKET", "stmt-2", "Shopping"),
            CreateExpense(12, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 100m, "COFFEE", "stmt-2", "Food & Dining"),
            CreateExpense(12, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 50m, "BOOK", "stmt-1", "Education"));
        await dbContext.SaveChangesAsync();

        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

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
        Assert.Contains("Toplam Harcama: 450,00 TRY", responseSender.Messages[0]);
        Assert.Contains("İşlem Sayısı: 3", responseSender.Messages[0]);
        Assert.Contains("*Ekstre Dönemleri*", responseSender.Messages[0]);
        Assert.Contains("1. 15.03.2026 - 16.03.2026 • 2 işlem • 400,00 TRY", responseSender.Messages[0]);
        Assert.Contains("2. 01.02.2026 - 01.02.2026 • 1 işlem • 50,00 TRY", responseSender.Messages[0]);
        Assert.Contains("*Kategoriler*", responseSender.Messages[0]);
        Assert.Contains("Shopping • 1 işlem • 300,00 TRY", responseSender.Messages[0]);
        Assert.Contains("Food & Dining • 1 işlem • 100,00 TRY", responseSender.Messages[0]);
        Assert.Contains("Education • 1 işlem • 50,00 TRY", responseSender.Messages[0]);
        Assert.Contains("Detay için: `/expense 1`", responseSender.Messages[0]);
        Assert.Contains("Kategori filtresi: `/expense 1 Other`", responseSender.Messages[0]);
        Assert.DoesNotContain("normal sohbette sorabilirsin", responseSender.Messages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsRegistrationMessage_WhenUserMissing()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsRegistrationMessage_WhenUserMissing));
        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

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

    [Fact]
    public async Task ExecuteAsync_ReturnsStatementTransactions_WhenStatementNumberProvided()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsStatementTransactions_WhenStatementNumberProvided));
        SeedUser(dbContext, 13, 1313);
        dbContext.Expenses.AddRange(
            CreateExpense(13, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 300m, "IYZICO/KAFEMATIK.COM.TR_[VIP]*", "stmt-2", "Food & Dining"),
            CreateExpense(13, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 100m, "COFFEE", "stmt-2", "Other"),
            CreateExpense(13, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 50m, "OLD", "stmt-1", "Other"));
        await dbContext.SaveChangesAsync();

        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense 1",
                    Chat = new Chat { Id = 1313 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("*📄 Ekstre 1*", responseSender.Messages[0]);
        Assert.Contains("Dönem: 15.03.2026 - 16.03.2026", responseSender.Messages[0]);
        Assert.Contains("Toplam: 400,00 TRY", responseSender.Messages[0]);
        Assert.Contains("*Kategoriler*", responseSender.Messages[0]);
        Assert.Contains("Food & Dining • 1 işlem • 300,00 TRY", responseSender.Messages[0]);
        Assert.Contains("Other • 1 işlem • 100,00 TRY", responseSender.Messages[0]);
        Assert.Contains($"15.03.2026 • IYZICO/\u200BKAFEMATIK.\u200BCOM.\u200BTR\\_\\[VIP\\]\\* • 300,00 TRY", responseSender.Messages[0]);
        Assert.DoesNotContain("KAFEMATIK.COM.TR", responseSender.Messages[0], StringComparison.Ordinal);
        Assert.Contains("16.03.2026 • COFFEE • 100,00 TRY", responseSender.Messages[0]);
        Assert.Contains("Kategori filtresi: `/expense 1 Other`", responseSender.Messages[0]);
        Assert.DoesNotContain("OLD", responseSender.Messages[0]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFilteredTransactions_WhenStatementCategoryProvided()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsFilteredTransactions_WhenStatementCategoryProvided));
        SeedUser(dbContext, 15, 1515);
        dbContext.Expenses.AddRange(
            CreateExpense(15, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 300m, "DINNER", "stmt-2", "Food & Dining"),
            CreateExpense(15, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 100m, "COFFEE", "stmt-2", "Other"),
            CreateExpense(15, new DateTime(2026, 3, 17, 0, 0, 0, DateTimeKind.Utc), 50m, "LUNCH", "stmt-2", "Food & Dining"));
        await dbContext.SaveChangesAsync();

        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense 1 Food & Dining",
                    Chat = new Chat { Id = 1515 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("Kategori: Food & Dining", responseSender.Messages[0]);
        Assert.Contains("Toplam: 350,00 TRY", responseSender.Messages[0]);
        Assert.Contains("İşlem Sayısı: 2", responseSender.Messages[0]);
        Assert.Contains("15.03.2026 • DINNER • 300,00 TRY", responseSender.Messages[0]);
        Assert.Contains("17.03.2026 • LUNCH • 50,00 TRY", responseSender.Messages[0]);
        Assert.DoesNotContain("COFFEE • 100,00 TRY", responseSender.Messages[0]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsHelpfulMessage_WhenStatementCategoryIsInvalid()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsHelpfulMessage_WhenStatementCategoryIsInvalid));
        SeedUser(dbContext, 16, 1616);
        dbContext.Expenses.AddRange(
            CreateExpense(16, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 300m, "DINNER", "stmt-2", "Food & Dining"),
            CreateExpense(16, new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), 100m, "COFFEE", "stmt-2", "Other"));
        await dbContext.SaveChangesAsync();

        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense 1 Travel",
                    Chat = new Chat { Id = 1616 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("*Geçersiz Kategori*", responseSender.Messages[0]);
        Assert.Contains("`Travel` bulunamadı.", responseSender.Messages[0]);
        Assert.Contains("- Food & Dining", responseSender.Messages[0]);
        Assert.Contains("- Other", responseSender.Messages[0]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsHelpfulMessage_WhenStatementNumberIsOutOfRange()
    {
        await using var dbContext = CreateDbContext(nameof(ExecuteAsync_ReturnsHelpfulMessage_WhenStatementNumberIsOutOfRange));
        SeedUser(dbContext, 14, 1414);
        dbContext.Expenses.AddRange(
            CreateExpense(14, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), 300m, "MARKET", "stmt-2", "Shopping"),
            CreateExpense(14, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 50m, "OLD", "stmt-1", "Other"));
        await dbContext.SaveChangesAsync();

        var responseSender = new FakeTelegramResponseSender();
        var command = CreateCommand(dbContext, responseSender);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/expense 3",
                    Chat = new Chat { Id = 1414 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("*Geçersiz Ekstre Numarası*", responseSender.Messages[0]);
        Assert.Contains("`/expense 3` bulunamadı.", responseSender.Messages[0]);
        Assert.Contains("1 ile 2 arasında bir numara kullan.", responseSender.Messages[0]);
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

    private static ExpenseCommand CreateCommand(ApplicationDbContext dbContext, FakeTelegramResponseSender responseSender)
    {
        return new ExpenseCommand(
            new FakeExpenseAnalysisService(),
            new ExpenseStatementBrowseService(dbContext),
            dbContext,
            responseSender,
            NullLogger<ExpenseCommand>.Instance);
    }

    private static ExpenseModel CreateExpense(
        int userId,
        DateTime expenseDate,
        decimal amount,
        string description,
        string? statementFingerprint = null,
        string category = "Other")
    {
        return new ExpenseModel
        {
            TelegramUserId = userId,
            ExpenseDate = expenseDate,
            Amount = amount,
            Currency = "TRY",
            Description = description,
            StatementFingerprint = statementFingerprint ?? Guid.NewGuid().ToString("N"),
            Category = category,
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
