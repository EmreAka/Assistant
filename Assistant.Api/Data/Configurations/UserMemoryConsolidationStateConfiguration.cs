using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class UserMemoryConsolidationStateConfiguration : IEntityTypeConfiguration<UserMemoryConsolidationState>
{
    public void Configure(EntityTypeBuilder<UserMemoryConsolidationState> builder)
    {
        builder.ToTable("user_memory_consolidation_states");

        builder.HasKey(x => x.TelegramUserId);

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .ValueGeneratedNever();

        builder.Property(x => x.LastConsolidatedChatTurnId)
            .HasColumnName("last_consolidated_chat_turn_id")
            .IsRequired();

        builder.Property(x => x.IsJobQueued)
            .HasColumnName("is_job_queued")
            .IsRequired();

        builder.Property(x => x.JobQueuedAtUtc)
            .HasColumnName("job_queued_at_utc");

        builder.Property(x => x.JobStartedAtUtc)
            .HasColumnName("job_started_at_utc");

        builder.Property(x => x.LastCompletedAtUtc)
            .HasColumnName("last_completed_at_utc");

        builder.Property(x => x.LastAttemptedAtUtc)
            .HasColumnName("last_attempted_at_utc");

        builder.Property(x => x.LastError)
            .HasColumnName("last_error")
            .HasColumnType("text")
            .IsRequired();

        builder.HasOne(x => x.TelegramUser)
            .WithOne(x => x.MemoryConsolidationState)
            .HasForeignKey<UserMemoryConsolidationState>(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
