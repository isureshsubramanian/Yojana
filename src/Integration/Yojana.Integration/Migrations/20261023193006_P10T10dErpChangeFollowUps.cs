using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T10dErpChangeFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "deliberate_unit_histories_changed",
                schema: "integration",
                table: "erp_change_run",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "unit_histories_changed",
                schema: "integration",
                table: "erp_change_run",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "unit_history_checked_at",
                schema: "integration",
                table: "erp_change_run",
                type: "datetime2(3)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deliberate_unit_histories_changed",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropColumn(
                name: "unit_histories_changed",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropColumn(
                name: "unit_history_checked_at",
                schema: "integration",
                table: "erp_change_run");
        }
    }
}
