using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Api.Data.Configurations;

public class ChatTurnConfiguration : IEntityTypeConfiguration<ChatTurn>
{
    public void Configure(EntityTypeBuilder<ChatTurn> builder)
    {
        builder.ToTable("chat_turns");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .IsRequired();

        builder.Property(x => x.UserMessage)
            .HasColumnName("user_message")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.AssistantMessage)
            .HasColumnName("assistant_message")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.SearchVector)
            .HasColumnName("search_vector");

        builder.HasGeneratedTsVectorColumn(
            x => x.SearchVector,
            "simple",
            x => new { x.UserMessage, x.AssistantMessage });

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(x => new { x.TelegramUserId, x.CreatedAt })
            .HasDatabaseName("IX_chat_turns_telegram_user_id_created_at");

        builder.HasIndex(x => x.SearchVector)
            .HasMethod("GIN")
            .HasDatabaseName("IX_chat_turns_search_vector");

        builder.HasOne(x => x.TelegramUser)
            .WithMany(x => x.ChatTurns)
            .HasForeignKey(x => x.TelegramUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
