using Assistant.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.ToTable("reminders");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.ReminderId)
            .HasColumnName("reminder_id")
            .IsRequired();

        builder.HasIndex(x => x.ReminderId)
            .IsUnique();

        builder.Property(x => x.ChatId)
            .HasColumnName("chat_id")
            .IsRequired();

        builder.Property(x => x.TopicId)
            .HasColumnName("topic_id");

        builder.Property(x => x.Message)
            .HasColumnName("message")
            .IsRequired();

        builder.Property(x => x.IsRecurring)
            .HasColumnName("is_recurring")
            .IsRequired();

        builder.Property(x => x.CronExpression)
            .HasColumnName("cron_expression");

        builder.Property(x => x.RunAtUtc)
            .HasColumnName("run_at_utc");

        builder.Property(x => x.TimeZoneId)
            .HasColumnName("time_zone_id")
            .IsRequired();

        builder.Property(x => x.HangfireJobId)
            .HasColumnName("hangfire_job_id")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .IsRequired();

        builder.Property(x => x.LastError)
            .HasColumnName("last_error");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at");

        builder.Property(x => x.LastSentAtUtc)
            .HasColumnName("last_sent_at_utc");

        builder.HasIndex(x => new { x.ChatId, x.Status });
    }
}
