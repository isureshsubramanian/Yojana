using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T31LinkOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "replacement_links_using_override",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "migration_link_override",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    direction = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    replacement_reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    state = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    replacement_condition_allowed = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    price_policy = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    difference_amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    uploaded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    uploaded_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    upload_file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_link_override", x => new { x.tenant_id, x.source_key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_link_override_tenant_id_uploaded_at_source_key",
                schema: "migration",
                table: "migration_link_override",
                columns: new[] { "tenant_id", "uploaded_at", "source_key" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_link_override_tenant_id_source_key",
                schema: "migration",
                table: "migration_link_override",
                columns: new[] { "tenant_id", "source_key" },
                unique: true);

            migrationBuilder.AddTenantPolicy("migration", "migration_link_override");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "migration_link_override");

            migrationBuilder.DropTable(
                name: "migration_link_override",
                schema: "migration");

            migrationBuilder.DropColumn(
                name: "replacement_links_using_override",
                schema: "migration",
                table: "migration_apply_run");
        }
    }
}
