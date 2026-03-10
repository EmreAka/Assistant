using Assistant.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class UserMemoryConfiguration : IEntityTypeConfiguration<UserMemory>
{
    public void Configure(EntityTypeBuilder<UserMemory> builder)
    {
        builder.ToTable("user_memories");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .IsRequired();

        builder.HasIndex(x => x.TelegramUserId);

        builder.Property(x => x.Category)
            .HasColumnName("category")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Content)
            .HasColumnName("content")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.Importance)
            .HasColumnName("importance")
            .IsRequired();

        builder.Property(x => x.Embedding)
            .HasColumnName("embedding")
            .HasColumnType("vector(768)");

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired()
            .HasDefaultValue(UserMemoryStatuses.Active);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.LastUsedAt)
            .HasColumnName("last_used_at");

        builder.Property(x => x.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(x => x.ArchivedAt)
            .HasColumnName("archived_at");

        builder.Property(x => x.MergedIntoMemoryId)
            .HasColumnName("merged_into_memory_id");

        builder.Property(x => x.LastConsolidatedAt)
            .HasColumnName("last_consolidated_at");

        builder.HasIndex(x => new { x.TelegramUserId, x.Status });

        builder.HasOne(x => x.TelegramUser)
            .WithMany(x => x.Memories)
            .HasForeignKey(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.MergedIntoMemory)
            .WithMany(x => x.MergedMemories)
            .HasForeignKey(x => x.MergedIntoMemoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
