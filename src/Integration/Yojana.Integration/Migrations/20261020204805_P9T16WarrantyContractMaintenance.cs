using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T16WarrantyContractMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "incumbent_changed_at",
                schema: "integration",
                table: "coexistence_conflict",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "integration",
                table: "coexistence_conflict",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "field_changed_in_incumbent");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "incumbent_changed_at",
                schema: "integration",
                table: "coexistence_conflict");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "integration",
                table: "coexistence_conflict");
        }
    }
}
