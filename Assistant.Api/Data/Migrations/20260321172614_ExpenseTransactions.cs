using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM expenses;");

            migrationBuilder.DropColumn(
                name: "billing_period_end_date",
                table: "expenses");

            migrationBuilder.RenameColumn(
                name: "billing_period_start_date",
                table: "expenses",
                newName: "expense_date");

            migrationBuilder.AddColumn<string>(
                name: "statement_fingerprint",
                table: "expenses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_telegram_user_id_statement_fingerprint",
                table: "expenses",
                columns: new[] { "telegram_user_id", "statement_fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_expenses_telegram_user_id_statement_fingerprint",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "statement_fingerprint",
                table: "expenses");

            migrationBuilder.RenameColumn(
                name: "expense_date",
                table: "expenses",
                newName: "billing_period_start_date");

            migrationBuilder.AddColumn<DateTime>(
                name: "billing_period_end_date",
                table: "expenses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }
}
