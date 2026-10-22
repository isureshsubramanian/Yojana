using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T10ApplyCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "certificates_recorded",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "entitlements_written",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "replacement_links_written",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "certificates_recorded",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "entitlements_written",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "replacement_links_written",
                schema: "migration",
                table: "migration_apply_run");
        }
    }
}
