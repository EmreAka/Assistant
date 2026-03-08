using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateExpenseTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "raw_data",
                table: "expenses");

            migrationBuilder.RenameColumn(
                name: "transaction_date",
                table: "expenses",
                newName: "billing_period_start_date");

            migrationBuilder.AddColumn<DateTime>(
                name: "billing_period_end_date",
                table: "expenses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "billing_period_end_date",
                table: "expenses");

            migrationBuilder.RenameColumn(
                name: "billing_period_start_date",
                table: "expenses",
                newName: "transaction_date");

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "expenses",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "raw_data",
                table: "expenses",
                type: "text",
                nullable: true);
        }
    }
}
