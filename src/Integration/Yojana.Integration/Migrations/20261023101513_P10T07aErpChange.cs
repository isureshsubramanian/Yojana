using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T07aErpChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_change_run",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    target_connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    current_step = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    started_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    closed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    close_reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    master_data_remapped_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    units_compared = table.Column<int>(type: "int", nullable: false),
                    stock_compared_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_run", x => new { x.tenant_id, x.erp_change_run_id });
                    table.CheckConstraint("ck_erp_change_run_status", "status IN ('active','cancelled','completed')");
                });

            migrationBuilder.CreateTable(
                name: "erp_change_master_exception",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_master_data_exception_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    target_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    tax_id = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    country = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    problem = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    candidate_module_ids_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    candidate_labels_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    live_units_affected = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    resolved_module_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    resolved_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    resolution_note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_master_exception", x => new { x.tenant_id, x.erp_change_master_data_exception_id });
                    table.CheckConstraint("ck_erp_change_master_exception_status", "status IN ('open','mapped','not_needed')");
                    table.ForeignKey(
                        name: "fk_erp_change_master_exception_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "erp_change_step_check",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_step_check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    step = table.Column<int>(type: "int", nullable: false),
                    passed = table.Column<bool>(type: "bit", nullable: false),
                    counts_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    checked_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    checked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_step_check", x => new { x.tenant_id, x.erp_change_step_check_id });
                    table.ForeignKey(
                        name: "fk_erp_change_step_check_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "erp_change_stock_difference",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_stock_difference_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    identifier = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    item_name = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    reason_class = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    module_location = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    source_location = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    target_location = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    module_status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    source_status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    target_status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    has_live_entitlement = table.Column<bool>(type: "bit", nullable: false),
                    warranty_end = table.Column<DateOnly>(type: "date", nullable: true),
                    suggested_resolution = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    resolution_command = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    resolution_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    resolved_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    compared_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    is_current = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_stock_difference", x => new { x.tenant_id, x.erp_change_stock_difference_id });
                    table.CheckConstraint("ck_erp_change_stock_difference_status", "status IN ('open','resolved')");
                    table.ForeignKey(
                        name: "fk_erp_change_stock_difference_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "erp_change_stock_signoff",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_stock_signoff_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    residual_count = table.Column<int>(type: "int", nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    signed_off_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    signed_off_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_stock_signoff", x => new { x.tenant_id, x.erp_change_stock_signoff_id });
                    table.ForeignKey(
                        name: "fk_erp_change_stock_signoff_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "erp_change_transition",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_transition_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    from_step = table.Column<int>(type: "int", nullable: false),
                    to_step = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_transition", x => new { x.tenant_id, x.erp_change_transition_id });
                    table.ForeignKey(
                        name: "fk_erp_change_transition_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_master_exception_tenant_id_erp_change_run_id_status_live_units_affected_erp_change_master_data_exception_id",
                schema: "integration",
                table: "erp_change_master_exception",
                columns: new[] { "tenant_id", "erp_change_run_id", "status", "live_units_affected", "erp_change_master_data_exception_id" });

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_master_exception_tenant_id_erp_change_run_id_entity_kind_target_external_id",
                schema: "integration",
                table: "erp_change_master_exception",
                columns: new[] { "tenant_id", "erp_change_run_id", "entity_kind", "target_external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_run_tenant_id_started_at_erp_change_run_id",
                schema: "integration",
                table: "erp_change_run",
                columns: new[] { "tenant_id", "started_at", "erp_change_run_id" });

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_run_tenant_id_status",
                schema: "integration",
                table: "erp_change_run",
                columns: new[] { "tenant_id", "status" },
                unique: true,
                filter: "[status] = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_step_check_tenant_id_erp_change_run_id_step_checked_at_erp_change_step_check_id",
                schema: "integration",
                table: "erp_change_step_check",
                columns: new[] { "tenant_id", "erp_change_run_id", "step", "checked_at", "erp_change_step_check_id" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_stock_difference_run_queue",
                schema: "integration",
                table: "erp_change_stock_difference",
                columns: new[] { "tenant_id", "erp_change_run_id", "status", "has_live_entitlement", "reason_class", "erp_change_stock_difference_id" });

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_stock_difference_tenant_id_erp_change_run_id_unit_id_reason_class",
                schema: "integration",
                table: "erp_change_stock_difference",
                columns: new[] { "tenant_id", "erp_change_run_id", "unit_id", "reason_class" },
                unique: true,
                filter: "[is_current] = 1 AND [unit_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_stock_signoff_tenant_id_erp_change_run_id",
                schema: "integration",
                table: "erp_change_stock_signoff",
                columns: new[] { "tenant_id", "erp_change_run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_transition_tenant_id_erp_change_run_id_occurred_at_erp_change_transition_id",
                schema: "integration",
                table: "erp_change_transition",
                columns: new[] { "tenant_id", "erp_change_run_id", "occurred_at", "erp_change_transition_id" });

            foreach (var table in new[]
                     {
                         "erp_change_run", "erp_change_step_check", "erp_change_transition",
                         "erp_change_master_exception", "erp_change_stock_difference",
                         "erp_change_stock_signoff",
                     })
                migrationBuilder.AddTenantPolicy("integration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
                     {
                         "erp_change_stock_signoff", "erp_change_stock_difference",
                         "erp_change_master_exception", "erp_change_transition",
                         "erp_change_step_check", "erp_change_run",
                     })
                migrationBuilder.RemoveTenantPolicy("integration", table);

            migrationBuilder.DropTable(
                name: "erp_change_master_exception",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_step_check",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_stock_difference",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_stock_signoff",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_transition",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_run",
                schema: "integration");
        }
    }
}
