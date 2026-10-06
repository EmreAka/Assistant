using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class AgentEmotionStateConfiguration : IEntityTypeConfiguration<AgentEmotionState>
{
    public void Configure(EntityTypeBuilder<AgentEmotionState> builder)
    {
        builder.ToTable("agent_emotion_states");

        builder.HasKey(x => x.TelegramUserId);

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .ValueGeneratedNever();

        builder.Property(x => x.Valence)
            .HasColumnName("valence")
            .IsRequired();

        builder.Property(x => x.Arousal)
            .HasColumnName("arousal")
            .IsRequired();

        builder.Property(x => x.Mood)
            .HasColumnName("mood")
            .HasMaxLength(AgentEmotionState.MaxMoodLength)
            .IsRequired();

        builder.Property(x => x.Reason)
            .HasColumnName("reason")
            .HasMaxLength(AgentEmotionState.MaxReasonLength)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(x => x.LastTurnId)
            .HasColumnName("last_turn_id");

        builder.HasOne(x => x.TelegramUser)
            .WithOne()
            .HasForeignKey<AgentEmotionState>(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
