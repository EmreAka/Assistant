using Assistant.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class DeferredIntentConfiguration : IEntityTypeConfiguration<DeferredIntent>
{
    public void Configure(EntityTypeBuilder<DeferredIntent> builder)
    {
        builder.ToTable("deferred_intents");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.IntentId)
            .HasColumnName("intent_id")
            .IsRequired();

        builder.HasIndex(x => x.IntentId)
            .IsUnique();

        builder.Property(x => x.ChatId)
            .HasColumnName("chat_id")
            .IsRequired();

        builder.Property(x => x.OriginalInstruction)
            .HasColumnName("original_instruction")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.ScheduledAtUtc)
            .HasColumnName("scheduled_at_utc")
            .IsRequired();

        builder.Property(x => x.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(64)
            .IsRequired()
            .HasDefaultValue("UTC");

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.HangfireJobId)
            .HasColumnName("hangfire_job_id")
            .HasMaxLength(128);

        builder.Property(x => x.ExecutionResult)
            .HasColumnName("execution_result")
            .HasColumnType("text");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.ExecutedAtUtc)
            .HasColumnName("executed_at_utc");

        builder.HasIndex(x => new { x.ChatId, x.Status });
    }
}
