using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConvertMetricTimesToUtcMilliseconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "AtUtc",
                table: "MetricSamples",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "BucketStartUtc",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            // The rebuilt columns still hold the old text; convert it to ticks (T7.R7).
            migrationBuilder.Sql(DevOpsAgent.Infrastructure.Persistence.MetricTimeConversionSql.Up("MetricSamples", "AtUtc"));
            migrationBuilder.Sql(DevOpsAgent.Infrastructure.Persistence.MetricTimeConversionSql.Up("MetricRollups", "BucketStartUtc"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DevOpsAgent.Infrastructure.Persistence.MetricTimeConversionSql.Down("MetricSamples", "AtUtc"));
            migrationBuilder.Sql(DevOpsAgent.Infrastructure.Persistence.MetricTimeConversionSql.Down("MetricRollups", "BucketStartUtc"));
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "AtUtc",
                table: "MetricSamples",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "BucketStartUtc",
                table: "MetricRollups",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");
        }
    }
}
