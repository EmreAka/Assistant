using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class AssistantPersonalityConfiguration : IEntityTypeConfiguration<AssistantPersonality>
{
    public void Configure(EntityTypeBuilder<AssistantPersonality> builder)
    {
        builder.ToTable("assistant_personalities");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .IsRequired();

        builder.HasIndex(x => x.TelegramUserId)
            .IsUnique();

        builder.Property(x => x.PersonalityText)
            .HasColumnName("personality_text")
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue(string.Empty);

        builder.HasOne(x => x.TelegramUser)
            .WithOne(x => x.AssistantPersonality)
            .HasForeignKey<AssistantPersonality>(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
