using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntitlementTerm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "duration_unit",
                schema: "serialisation",
                table: "entitlement",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "months");

            migrationBuilder.AddColumn<int>(
                name: "duration_value",
                schema: "serialisation",
                table: "entitlement",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "end_inclusive",
                schema: "serialisation",
                table: "entitlement",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "end_of_month_rule",
                schema: "serialisation",
                table: "entitlement",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "clamp");

            migrationBuilder.AddColumn<DateOnly>(
                name: "start_latest",
                schema: "serialisation",
                table: "entitlement",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "duration_unit",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "duration_value",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "end_inclusive",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "end_of_month_rule",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "start_latest",
                schema: "serialisation",
                table: "entitlement");
        }
    }
}
