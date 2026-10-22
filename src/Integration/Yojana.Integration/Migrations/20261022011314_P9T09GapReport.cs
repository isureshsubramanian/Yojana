using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T09GapReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cover_gaps",
                schema: "migration",
                table: "migration_apply_run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "gap_report_run_id",
                schema: "migration",
                table: "migration_apply_run",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cover_gaps",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "gap_report_run_id",
                schema: "migration",
                table: "migration_apply_run");
        }
    }
}
