using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServerSudoPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedSudoPassword",
                table: "Servers",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SudoPasswordRejectedAtUtc",
                table: "Servers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedSudoPassword",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "SudoPasswordRejectedAtUtc",
                table: "Servers");
        }
    }
}
