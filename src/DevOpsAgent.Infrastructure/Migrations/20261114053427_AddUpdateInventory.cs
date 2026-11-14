using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdatePackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CurrentVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CandidateVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Repository = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsSecurity = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdatePackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpdatePackages_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UpdateScans",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PackageManager = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateScans", x => x.ServerId);
                    table.ForeignKey(
                        name: "FK_UpdateScans_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdatePackages_ServerId_Name",
                table: "UpdatePackages",
                columns: new[] { "ServerId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdatePackages");

            migrationBuilder.DropTable(
                name: "UpdateScans");
        }
    }
}
