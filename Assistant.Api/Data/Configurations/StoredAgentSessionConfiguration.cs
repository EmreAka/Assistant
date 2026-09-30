using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class StoredAgentSessionConfiguration : IEntityTypeConfiguration<StoredAgentSession>
{
    public void Configure(EntityTypeBuilder<StoredAgentSession> builder)
    {
        builder.ToTable("agent_sessions");

        builder.HasKey(x => x.ChatId);

        builder.Property(x => x.ChatId)
            .HasColumnName("chat_id")
            .ValueGeneratedNever();

        // json, not jsonb: jsonb reorders object keys, and System.Text.Json rejects a "$type" discriminator
        // (message contents) that is not the first property, so the session would no longer deserialize.
        builder.Property(x => x.Session)
            .HasColumnName("session")
            .HasColumnType("json")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
