using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T34LoadReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "conflict_id",
                schema: "migration",
                table: "migration_delta_item",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "watermarks_before",
                schema: "migration",
                table: "migration_apply_run",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conflict_id",
                schema: "migration",
                table: "migration_delta_item");

            migrationBuilder.DropColumn(
                name: "watermarks_before",
                schema: "migration",
                table: "migration_apply_run");
        }
    }
}
