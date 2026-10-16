using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefurbishedDisclosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "refurbished_floor_applied",
                schema: "serialisation",
                table: "entitlement",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "refurbished_floor_months",
                schema: "serialisation",
                table: "entitlement",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refurbished_floor_source",
                schema: "serialisation",
                table: "entitlement",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refurbished_matched_row",
                schema: "serialisation",
                table: "entitlement",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refurbished_statement_kind",
                schema: "serialisation",
                table: "entitlement",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "refurbisher_party_ref",
                schema: "serialisation",
                table: "entitlement",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refurbished_floor_applied",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "refurbished_floor_months",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "refurbished_floor_source",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "refurbished_matched_row",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "refurbished_statement_kind",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "refurbisher_party_ref",
                schema: "serialisation",
                table: "entitlement");
        }
    }
}
