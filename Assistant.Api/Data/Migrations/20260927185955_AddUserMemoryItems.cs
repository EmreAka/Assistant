using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMemoryItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "memory_processed_at",
                table: "chat_turns",
                type: "timestamp with time zone",
                nullable: true);

            // Turns already consolidated into the manifest are covered by the one-time manifest
            // import, so mark them processed. Later turns stay NULL and are extracted normally.
            migrationBuilder.Sql("""
                UPDATE chat_turns AS t
                SET memory_processed_at = now()
                FROM user_memory_consolidation_states AS s
                WHERE s.telegram_user_id = t.telegram_user_id
                  AND t.id <= s.last_consolidated_chat_turn_id;
                """);

            migrationBuilder.CreateTable(
                name: "user_memory_items",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_user_id = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_core = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    source_turn_ids = table.Column<int[]>(type: "integer[]", nullable: false),
                    superseded_by_id = table.Column<int>(type: "integer", nullable: true),
                    change_reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_memory_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_memory_items_telegram_users_telegram_user_id",
                        column: x => x.telegram_user_id,
                        principalTable: "telegram_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_memory_items_user_memory_items_superseded_by_id",
                        column: x => x.superseded_by_id,
                        principalTable: "user_memory_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_memory_items_superseded_by_id",
                table: "user_memory_items",
                column: "superseded_by_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_memory_items_telegram_user_id_status",
                table: "user_memory_items",
                columns: new[] { "telegram_user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_memory_items");

            migrationBuilder.DropColumn(
                name: "memory_processed_at",
                table: "chat_turns");
        }
    }
}
