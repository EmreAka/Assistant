using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatTurnEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "chat_turns",
                type: "vector(768)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding",
                table: "chat_turns");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
