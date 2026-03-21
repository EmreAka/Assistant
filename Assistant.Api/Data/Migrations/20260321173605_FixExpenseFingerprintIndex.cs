using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixExpenseFingerprintIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_expenses_telegram_user_id_statement_fingerprint",
                table: "expenses");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_telegram_user_id_statement_fingerprint",
                table: "expenses",
                columns: new[] { "telegram_user_id", "statement_fingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_expenses_telegram_user_id_statement_fingerprint",
                table: "expenses");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_telegram_user_id_statement_fingerprint",
                table: "expenses",
                columns: new[] { "telegram_user_id", "statement_fingerprint" },
                unique: true);
        }
    }
}
