using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMemoryConsolidationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_memory_consolidation_states",
                columns: table => new
                {
                    telegram_user_id = table.Column<int>(type: "integer", nullable: false),
                    last_consolidated_chat_turn_id = table.Column<int>(type: "integer", nullable: false),
                    is_job_queued = table.Column<bool>(type: "boolean", nullable: false),
                    job_queued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    job_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_memory_consolidation_states", x => x.telegram_user_id);
                    table.ForeignKey(
                        name: "FK_user_memory_consolidation_states_telegram_users_telegram_us~",
                        column: x => x.telegram_user_id,
                        principalTable: "telegram_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_memory_consolidation_states");
        }
    }
}
