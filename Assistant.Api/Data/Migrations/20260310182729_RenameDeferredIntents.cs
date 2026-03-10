using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameDeferredIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DeferredIntents",
                table: "DeferredIntents");

            migrationBuilder.RenameTable(
                name: "DeferredIntents",
                newName: "deferred_intents");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "deferred_intents",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "deferred_intents",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TimeZoneId",
                table: "deferred_intents",
                newName: "time_zone_id");

            migrationBuilder.RenameColumn(
                name: "ScheduledAtUtc",
                table: "deferred_intents",
                newName: "scheduled_at_utc");

            migrationBuilder.RenameColumn(
                name: "OriginalInstruction",
                table: "deferred_intents",
                newName: "original_instruction");

            migrationBuilder.RenameColumn(
                name: "IntentId",
                table: "deferred_intents",
                newName: "intent_id");

            migrationBuilder.RenameColumn(
                name: "HangfireJobId",
                table: "deferred_intents",
                newName: "hangfire_job_id");

            migrationBuilder.RenameColumn(
                name: "ExecutionResult",
                table: "deferred_intents",
                newName: "execution_result");

            migrationBuilder.RenameColumn(
                name: "ExecutedAtUtc",
                table: "deferred_intents",
                newName: "executed_at_utc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "deferred_intents",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "ChatId",
                table: "deferred_intents",
                newName: "chat_id");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "deferred_intents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "time_zone_id",
                table: "deferred_intents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "hangfire_job_id",
                table: "deferred_intents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_deferred_intents",
                table: "deferred_intents",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "IX_deferred_intents_chat_id_status",
                table: "deferred_intents",
                columns: new[] { "chat_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_deferred_intents_intent_id",
                table: "deferred_intents",
                column: "intent_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_deferred_intents",
                table: "deferred_intents");

            migrationBuilder.DropIndex(
                name: "IX_deferred_intents_chat_id_status",
                table: "deferred_intents");

            migrationBuilder.DropIndex(
                name: "IX_deferred_intents_intent_id",
                table: "deferred_intents");

            migrationBuilder.RenameTable(
                name: "deferred_intents",
                newName: "DeferredIntents");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "DeferredIntents",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "DeferredIntents",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "time_zone_id",
                table: "DeferredIntents",
                newName: "TimeZoneId");

            migrationBuilder.RenameColumn(
                name: "scheduled_at_utc",
                table: "DeferredIntents",
                newName: "ScheduledAtUtc");

            migrationBuilder.RenameColumn(
                name: "original_instruction",
                table: "DeferredIntents",
                newName: "OriginalInstruction");

            migrationBuilder.RenameColumn(
                name: "intent_id",
                table: "DeferredIntents",
                newName: "IntentId");

            migrationBuilder.RenameColumn(
                name: "hangfire_job_id",
                table: "DeferredIntents",
                newName: "HangfireJobId");

            migrationBuilder.RenameColumn(
                name: "execution_result",
                table: "DeferredIntents",
                newName: "ExecutionResult");

            migrationBuilder.RenameColumn(
                name: "executed_at_utc",
                table: "DeferredIntents",
                newName: "ExecutedAtUtc");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "DeferredIntents",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "chat_id",
                table: "DeferredIntents",
                newName: "ChatId");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "DeferredIntents",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                table: "DeferredIntents",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldDefaultValue: "UTC");

            migrationBuilder.AlterColumn<string>(
                name: "HangfireJobId",
                table: "DeferredIntents",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DeferredIntents",
                table: "DeferredIntents",
                column: "Id");
        }
    }
}
