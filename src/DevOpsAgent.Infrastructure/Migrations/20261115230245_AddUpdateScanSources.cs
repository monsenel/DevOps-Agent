using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateScanSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourcesJson",
                table: "UpdateScans",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourcesJson",
                table: "UpdateScans");
        }
    }
}
