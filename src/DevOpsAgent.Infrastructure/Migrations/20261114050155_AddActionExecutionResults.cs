using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevOpsAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActionExecutionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAtUtc",
                table: "ActionRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "ActionRecords",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandsJson",
                table: "ActionRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "ActionRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMilliseconds",
                table: "ActionRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExitCode",
                table: "ActionRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultsJson",
                table: "ActionRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAtUtc",
                table: "ActionRecords");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "ActionRecords");

            migrationBuilder.DropColumn(
                name: "CommandsJson",
                table: "ActionRecords");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "ActionRecords");

            migrationBuilder.DropColumn(
                name: "DurationMilliseconds",
                table: "ActionRecords");

            migrationBuilder.DropColumn(
                name: "ExitCode",
                table: "ActionRecords");

            migrationBuilder.DropColumn(
                name: "ResultsJson",
                table: "ActionRecords");
        }
    }
}
