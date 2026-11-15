using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServerSudoAuthFailedSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SudoAuthFailedSinceUtc",
                table: "Servers",
                type: "TEXT",
                nullable: true);

            // A server already paused by a rejected sudo password gets its sudo-auth-failed alert (D-040 amendment 2).
            migrationBuilder.Sql(
                "UPDATE \"Servers\" SET \"SudoAuthFailedSinceUtc\" = \"SudoPasswordRejectedAtUtc\" "
                + "WHERE \"SudoPasswordRejectedAtUtc\" IS NOT NULL AND \"ProtectedSudoPassword\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SudoAuthFailedSinceUtc",
                table: "Servers");
        }
    }
}
