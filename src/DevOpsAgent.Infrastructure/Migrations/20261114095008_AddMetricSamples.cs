using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Metric = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    BucketStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Minimum = table.Column<double>(type: "REAL", nullable: false),
                    Average = table.Column<double>(type: "REAL", nullable: false),
                    Maximum = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricRollups_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricSamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Metric = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricSamples_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_ServerId_Metric_BucketStartUtc",
                table: "MetricRollups",
                columns: new[] { "ServerId", "Metric", "BucketStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricSamples_ServerId_Metric_AtUtc",
                table: "MetricSamples",
                columns: new[] { "ServerId", "Metric", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricRollups");

            migrationBuilder.DropTable(
                name: "MetricSamples");
        }
    }
}
