using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T10bDeltaLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_key",
                schema: "migration",
                table: "migration_plan_unit",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_key_hash",
                schema: "migration",
                table: "migration_plan_unit",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "source_clock_at_start",
                schema: "migration",
                table: "migration_batch",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "already_in_module",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "base_load_id",
                schema: "migration",
                table: "migration_apply_run",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "cover_refused",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "cover_replaced",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "gone_from_source",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "migration",
                table: "migration_apply_run",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "initial");

            migrationBuilder.AddColumn<long>(
                name: "owner_differs",
                schema: "migration",
                table: "migration_apply_run",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "watermark_handed_over_from",
                schema: "migration",
                table: "migration_apply_run",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "watermark_note",
                schema: "migration",
                table: "migration_apply_run",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "migration_delta_item",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    serial = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    previous_cover_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    previous_cover_load_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_delta_item", x => new { x.tenant_id, x.item_id });
                    table.CheckConstraint("ck_migration_delta_item_kind", "kind IN ('new_unit','already_in_module','cover_replaced','cover_refused','owner_differs','gone_from_source')");
                    table.ForeignKey(
                        name: "fk_migration_delta_item_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_apply_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_loaded_unit",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_key_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    load_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cover_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    cover_load_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    owner_party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    loaded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_loaded_unit", x => new { x.tenant_id, x.connection_id, x.source_key_hash });
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_unit_tenant_id_map_run_id_source_key_hash",
                schema: "migration",
                table: "migration_plan_unit",
                columns: new[] { "tenant_id", "map_run_id", "source_key_hash" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_apply_run_kind",
                schema: "migration",
                table: "migration_apply_run",
                sql: "kind IN ('initial','delta')");

            migrationBuilder.CreateIndex(
                name: "ix_migration_delta_item_tenant_id_run_id_item_id",
                schema: "migration",
                table: "migration_delta_item",
                columns: new[] { "tenant_id", "run_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_delta_item_tenant_id_run_id_kind_item_id",
                schema: "migration",
                table: "migration_delta_item",
                columns: new[] { "tenant_id", "run_id", "kind", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_loaded_unit_tenant_id_cover_load_id",
                schema: "migration",
                table: "migration_loaded_unit",
                columns: new[] { "tenant_id", "cover_load_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_loaded_unit_tenant_id_load_id",
                schema: "migration",
                table: "migration_loaded_unit",
                columns: new[] { "tenant_id", "load_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_loaded_unit_tenant_id_unit_id",
                schema: "migration",
                table: "migration_loaded_unit",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.AddTenantPolicy("migration", "migration_loaded_unit");
            migrationBuilder.AddTenantPolicy("migration", "migration_delta_item");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "migration_delta_item");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_loaded_unit");

            migrationBuilder.DropTable(
                name: "migration_delta_item",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_loaded_unit",
                schema: "migration");

            migrationBuilder.DropIndex(
                name: "ix_migration_plan_unit_tenant_id_map_run_id_source_key_hash",
                schema: "migration",
                table: "migration_plan_unit");

            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_apply_run_kind",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "source_key",
                schema: "migration",
                table: "migration_plan_unit");

            migrationBuilder.DropColumn(
                name: "source_key_hash",
                schema: "migration",
                table: "migration_plan_unit");

            migrationBuilder.DropColumn(
                name: "source_clock_at_start",
                schema: "migration",
                table: "migration_batch");

            migrationBuilder.DropColumn(
                name: "already_in_module",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "base_load_id",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "cover_refused",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "cover_replaced",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "gone_from_source",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "owner_differs",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "watermark_handed_over_from",
                schema: "migration",
                table: "migration_apply_run");

            migrationBuilder.DropColumn(
                name: "watermark_note",
                schema: "migration",
                table: "migration_apply_run");
        }
    }
}
