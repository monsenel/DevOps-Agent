using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LlmProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    ContextWindow = table.Column<int>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<double>(type: "REAL", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    NativeToolCallingEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StreamingEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RedactSecretsBeforeSending = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastTestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmProviders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviders_IsDefault",
                table: "LlmProviders",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmProviders");
        }
    }
}
