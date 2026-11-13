using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistProviderTestSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastTestError",
                table: "LlmProviders",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastTestModelCount",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastTestStreamingSupported",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastTestToolCallingSupported",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTestError",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastTestModelCount",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastTestStreamingSupported",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastTestToolCallingSupported",
                table: "LlmProviders");
        }
    }
}
