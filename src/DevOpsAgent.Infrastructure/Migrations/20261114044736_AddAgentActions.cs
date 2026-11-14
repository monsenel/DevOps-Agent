using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ServerIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ArgumentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionRecords_Status_CreatedAtUtc",
                table: "ActionRecords",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionRecords");
        }
    }
}
