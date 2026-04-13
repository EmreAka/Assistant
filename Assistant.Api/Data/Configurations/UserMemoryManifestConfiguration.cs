using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class UserMemoryManifestConfiguration : IEntityTypeConfiguration<UserMemoryManifest>
{
    public void Configure(EntityTypeBuilder<UserMemoryManifest> builder)
    {
        builder.ToTable("user_memory_manifests");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .IsRequired();

        builder.HasIndex(x => x.TelegramUserId);

        builder.Property(x => x.Content)
            .HasColumnName("content")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(x => new { x.TelegramUserId, x.IsActive })
            .HasDatabaseName("IX_user_memory_manifests_telegram_user_id_is_active");

        builder.HasOne(x => x.TelegramUser)
            .WithMany()
            .HasForeignKey(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
