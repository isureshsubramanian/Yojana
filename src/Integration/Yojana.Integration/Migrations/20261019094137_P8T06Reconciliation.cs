using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T06Reconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "on_hand_status_mapping_json",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "reconciliation_run",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    range_start = table.Column<DateOnly>(type: "date", nullable: false),
                    range_end = table.Column<DateOnly>(type: "date", nullable: false),
                    trigger = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    mismatch_count = table.Column<int>(type: "int", nullable: false),
                    location_mismatch_count = table.Column<int>(type: "int", nullable: false),
                    failure = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_run", x => new { x.tenant_id, x.reconciliation_run_id });
                    table.CheckConstraint("ck_reconciliation_run_status", "status IN ('running','clean','mismatch','failed')");
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_entity_day",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_entity_day_result_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source_count = table.Column<long>(type: "bigint", nullable: false),
                    module_count = table.Column<long>(type: "bigint", nullable: false),
                    source_quantity = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    module_quantity = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    source_distinct_serials = table.Column<long>(type: "bigint", nullable: false),
                    module_distinct_serials = table.Column<long>(type: "bigint", nullable: false),
                    source_key_field_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    module_key_field_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ranged_read_supported = table.Column<bool>(type: "bit", nullable: false),
                    backfill_job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    dead_letter_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_entity_day", x => new { x.tenant_id, x.reconciliation_entity_day_result_id });
                    table.CheckConstraint("ck_reconciliation_entity_day_status", "status IN ('clean','mismatch')");
                    table.ForeignKey(
                        name: "fk_reconciliation_entity_day_tenant_id_reconciliation_run_id",
                        columns: x => new { x.tenant_id, x.reconciliation_run_id },
                        principalSchema: "integration",
                        principalTable: "reconciliation_run",
                        principalColumns: new[] { "tenant_id", "reconciliation_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_location",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_location_result_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    item_name = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    location_code = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    location_name = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    source_on_hand = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    module_unit_count = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_location", x => new { x.tenant_id, x.reconciliation_location_result_id });
                    table.CheckConstraint("ck_reconciliation_location_status", "status IN ('clean','mismatch')");
                    table.ForeignKey(
                        name: "fk_reconciliation_location_tenant_id_reconciliation_run_id",
                        columns: x => new { x.tenant_id, x.reconciliation_run_id },
                        principalSchema: "integration",
                        principalTable: "reconciliation_run",
                        principalColumns: new[] { "tenant_id", "reconciliation_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_entity_day_tenant_id_connection_id_entity_business_date",
                schema: "integration",
                table: "reconciliation_entity_day",
                columns: new[] { "tenant_id", "connection_id", "entity", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_entity_day_tenant_id_reconciliation_run_id_business_date_entity_reconciliation_entity_day_result_id",
                schema: "integration",
                table: "reconciliation_entity_day",
                columns: new[] { "tenant_id", "reconciliation_run_id", "business_date", "entity", "reconciliation_entity_day_result_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_location_tenant_id_reconciliation_run_id_item_id_location_id_reconciliation_location_result_id",
                schema: "integration",
                table: "reconciliation_location",
                columns: new[] { "tenant_id", "reconciliation_run_id", "item_id", "location_id", "reconciliation_location_result_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_run_tenant_id_connection_id_started_at_reconciliation_run_id",
                schema: "integration",
                table: "reconciliation_run",
                columns: new[] { "tenant_id", "connection_id", "started_at", "reconciliation_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_run_tenant_id_started_at_reconciliation_run_id",
                schema: "integration",
                table: "reconciliation_run",
                columns: new[] { "tenant_id", "started_at", "reconciliation_run_id" });

            foreach (var table in new[] { "reconciliation_run", "reconciliation_entity_day",
                         "reconciliation_location" })
                migrationBuilder.AddTenantPolicy("integration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "reconciliation_location", "reconciliation_entity_day",
                         "reconciliation_run" })
                migrationBuilder.RemoveTenantPolicy("integration", table);
            migrationBuilder.DropTable(
                name: "reconciliation_entity_day",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "reconciliation_location",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "reconciliation_run",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "on_hand_status_mapping_json",
                schema: "integration",
                table: "connection_gateway_setting");
        }
    }
}
