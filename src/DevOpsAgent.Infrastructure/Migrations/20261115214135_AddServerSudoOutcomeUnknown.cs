using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServerSudoOutcomeUnknown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SudoPasswordAttemptStartedAtUtc",
                table: "Servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SudoPasswordOutcomeUnknownAtUtc",
                table: "Servers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SudoPasswordAttemptStartedAtUtc",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "SudoPasswordOutcomeUnknownAtUtc",
                table: "Servers");
        }
    }
}
