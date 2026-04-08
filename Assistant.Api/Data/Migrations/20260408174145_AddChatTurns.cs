using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_turns",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_user_id = table.Column<int>(type: "integer", nullable: false),
                    user_message = table.Column<string>(type: "text", nullable: false),
                    assistant_message = table.Column<string>(type: "text", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "user_message", "assistant_message" }),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_turns", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_turns_telegram_users_telegram_user_id",
                        column: x => x.telegram_user_id,
                        principalTable: "telegram_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_search_vector",
                table: "chat_turns",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_telegram_user_id_created_at",
                table: "chat_turns",
                columns: new[] { "telegram_user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_turns");
        }
    }
}
