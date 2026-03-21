using Assistant.Api.Features.Expense.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.ToTable("expenses");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .IsRequired();

        builder.Property(x => x.ExpenseDate)
            .HasColumnName("expense_date")
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasColumnName("amount")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("currency")
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(x => x.Description)
            .HasColumnName("description")
            .IsRequired();

        builder.Property(x => x.StatementFingerprint)
            .HasColumnName("statement_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(x => x.TelegramUserId)
            .HasDatabaseName("IX_expenses_telegram_user_id");

        builder.HasIndex(x => new { x.TelegramUserId, x.StatementFingerprint })
            .HasDatabaseName("IX_expenses_telegram_user_id_statement_fingerprint");

        builder.HasOne(x => x.TelegramUser)
            .WithMany(u => u.Expenses)
            .HasForeignKey(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
