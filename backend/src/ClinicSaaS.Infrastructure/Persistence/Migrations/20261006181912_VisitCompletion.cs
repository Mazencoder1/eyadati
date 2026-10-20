using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VisitCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "Visits",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompletedByUserId",
                table: "Visits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Visits",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Open");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Visits_Completion",
                table: "Visits",
                sql: "([Status] = 'Completed' AND [CompletedAtUtc] IS NOT NULL) OR ([Status] <> 'Completed' AND [CompletedAtUtc] IS NULL AND [CompletedByUserId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Visits_Status",
                table: "Visits",
                sql: "[Status] IN ('Open','Completed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Visits_Completion",
                table: "Visits");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Visits_Status",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "CompletedByUserId",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Visits");
        }
    }
}
