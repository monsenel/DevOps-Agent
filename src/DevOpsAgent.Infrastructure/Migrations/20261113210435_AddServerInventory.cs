using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServerInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    KeyFile = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    ProtectedKeyPassphrase = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    JumpServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Group = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SudoCapable = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OsInfo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Servers_Servers_JumpServerId",
                        column: x => x.JumpServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Servers_JumpServerId",
                table: "Servers",
                column: "JumpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_Name",
                table: "Servers",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Servers");
        }
    }
}
