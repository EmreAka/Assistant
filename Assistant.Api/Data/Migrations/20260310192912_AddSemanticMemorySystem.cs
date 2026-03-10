using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticMemorySystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "user_memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "user_memories",
                type: "vector(768)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_consolidated_at",
                table: "user_memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "merged_into_memory_id",
                table: "user_memories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "user_memories",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.CreateIndex(
                name: "IX_user_memories_merged_into_memory_id",
                table: "user_memories",
                column: "merged_into_memory_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_memories_telegram_user_id_status",
                table: "user_memories",
                columns: new[] { "telegram_user_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "FK_user_memories_user_memories_merged_into_memory_id",
                table: "user_memories",
                column: "merged_into_memory_id",
                principalTable: "user_memories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_memories_user_memories_merged_into_memory_id",
                table: "user_memories");

            migrationBuilder.DropIndex(
                name: "IX_user_memories_merged_into_memory_id",
                table: "user_memories");

            migrationBuilder.DropIndex(
                name: "IX_user_memories_telegram_user_id_status",
                table: "user_memories");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "user_memories");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "user_memories");

            migrationBuilder.DropColumn(
                name: "last_consolidated_at",
                table: "user_memories");

            migrationBuilder.DropColumn(
                name: "merged_into_memory_id",
                table: "user_memories");

            migrationBuilder.DropColumn(
                name: "status",
                table: "user_memories");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
