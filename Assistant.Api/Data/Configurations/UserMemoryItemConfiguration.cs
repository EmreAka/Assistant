using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class UserMemoryItemConfiguration : IEntityTypeConfiguration<UserMemoryItem>
{
    public void Configure(EntityTypeBuilder<UserMemoryItem> builder)
    {
        builder.ToTable("user_memory_items");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .IsRequired();

        builder.Property(x => x.Text)
            .HasColumnName("text")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.Category)
            .HasColumnName("category")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.IsCore)
            .HasColumnName("is_core")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.Embedding)
            .HasColumnName("embedding")
            .HasColumnType("vector(768)")
            .IsRequired();

        builder.Property(x => x.SourceTurnIds)
            .HasColumnName("source_turn_ids")
            .IsRequired();

        builder.Property(x => x.SupersededById)
            .HasColumnName("superseded_by_id");

        builder.Property(x => x.ChangeReason)
            .HasColumnName("change_reason")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(x => x.LastConfirmedAt)
            .HasColumnName("last_confirmed_at")
            .IsRequired();

        builder.HasIndex(x => new { x.TelegramUserId, x.Status })
            .HasDatabaseName("IX_user_memory_items_telegram_user_id_status");

        builder.HasOne(x => x.TelegramUser)
            .WithMany()
            .HasForeignKey(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.SupersededBy)
            .WithMany()
            .HasForeignKey(x => x.SupersededById)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
