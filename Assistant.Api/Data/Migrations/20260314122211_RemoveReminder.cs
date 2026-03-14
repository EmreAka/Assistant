using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cron_expression = table.Column<string>(type: "text", nullable: true),
                    hangfire_job_id = table.Column<string>(type: "text", nullable: false),
                    is_recurring = table.Column<bool>(type: "boolean", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    last_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    reminder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    time_zone_id = table.Column<string>(type: "text", nullable: false),
                    topic_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminders_chat_id_status",
                table: "reminders",
                columns: new[] { "chat_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_reminders_reminder_id",
                table: "reminders",
                column: "reminder_id",
                unique: true);
        }
    }
}
