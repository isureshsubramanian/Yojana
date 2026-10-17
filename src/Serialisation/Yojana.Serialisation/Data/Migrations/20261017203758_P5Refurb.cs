using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5Refurb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refurbishment_work_order",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    open_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_number = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    target_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_rma_line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    source_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_document_line = table.Column<int>(type: "int", nullable: true),
                    selection_kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    selection_reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    cycle_number = table.Column<int>(type: "int", nullable: false),
                    identity_policy = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    max_cycles = table.Column<int>(type: "int", nullable: false),
                    replacement_policy_code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    life_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    as_built_changed = table.Column<bool>(type: "bit", nullable: false),
                    opened_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    opened_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    opening_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    release_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    released_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    released_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    released_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    released_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    grade = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    cancel_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    cancelled_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    cancellation_reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    cancelled_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refurbishment_work_order", x => new { x.tenant_id, x.work_order_id });
                    table.CheckConstraint("ck_refurbishment_work_order_cycles", "cycle_number >= 1 AND max_cycles >= 1");
                    table.CheckConstraint("ck_refurbishment_work_order_identity", "identity_policy IN ('same_unit_same_serial', 'same_unit_new_serial', 'new_unit')");
                    table.CheckConstraint("ck_refurbishment_work_order_kind", "kind IN ('refurbish', 'remanufacture', 'rework')");
                    table.CheckConstraint("ck_refurbishment_work_order_status", "status IN ('open', 'released', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "refurbishment_cost",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cost_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    posted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    posted_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refurbishment_cost", x => new { x.tenant_id, x.cost_id });
                    table.CheckConstraint("ck_refurbishment_cost_amount", "amount >= 0");
                    table.CheckConstraint("ck_refurbishment_cost_kind", "kind IN ('graded_receipt', 'part', 'labour', 'other')");
                    table.ForeignKey(
                        name: "fk_refurbishment_cost_tenant_id_work_order_id",
                        columns: x => new { x.tenant_id, x.work_order_id },
                        principalSchema: "serialisation",
                        principalTable: "refurbishment_work_order",
                        principalColumns: new[] { "tenant_id", "work_order_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refurbishment_labour",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    labour_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    hours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    hourly_rate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    technician_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cost_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refurbishment_labour", x => new { x.tenant_id, x.labour_id });
                    table.CheckConstraint("ck_refurbishment_labour_amounts", "hours > 0 AND hourly_rate >= 0 AND amount >= 0");
                    table.ForeignKey(
                        name: "fk_refurbishment_labour_tenant_id_work_order_id",
                        columns: x => new { x.tenant_id, x.work_order_id },
                        principalSchema: "serialisation",
                        principalTable: "refurbishment_work_order",
                        principalColumns: new[] { "tenant_id", "work_order_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refurbishment_operation",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    technician_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    planned_hours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    actual_hours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    complete_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    signed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    signed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    added_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    completed_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refurbishment_operation", x => new { x.tenant_id, x.operation_id });
                    table.CheckConstraint("ck_refurbishment_operation_status", "status IN ('open', 'completed')");
                    table.ForeignKey(
                        name: "fk_refurbishment_operation_tenant_id_work_order_id",
                        columns: x => new { x.tenant_id, x.work_order_id },
                        principalSchema: "serialisation",
                        principalTable: "refurbishment_work_order",
                        principalColumns: new[] { "tenant_id", "work_order_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refurbishment_part",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    part_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    action = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    child_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    child_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    child_lot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    slot = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    destination = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    destination_location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    vendor_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    assembly_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    unit_cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refurbishment_part", x => new { x.tenant_id, x.part_id });
                    table.CheckConstraint("ck_refurbishment_part_action", "action IN ('removed', 'installed')");
                    table.CheckConstraint("ck_refurbishment_part_destination", "destination IS NULL OR destination IN ('spares', 'scrap', 'vendor_return')");
                    table.CheckConstraint("ck_refurbishment_part_identity", "(child_unit_id IS NOT NULL AND child_item_id IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_refurbishment_part_tenant_id_work_order_id",
                        columns: x => new { x.tenant_id, x.work_order_id },
                        principalSchema: "serialisation",
                        principalTable: "refurbishment_work_order",
                        principalColumns: new[] { "tenant_id", "work_order_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_cost_tenant_id_work_order_id_posted_at",
                schema: "serialisation",
                table: "refurbishment_cost",
                columns: new[] { "tenant_id", "work_order_id", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_cost_tenant_id_command_id",
                schema: "serialisation",
                table: "refurbishment_cost",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_labour_tenant_id_work_order_id_recorded_at",
                schema: "serialisation",
                table: "refurbishment_labour",
                columns: new[] { "tenant_id", "work_order_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_labour_tenant_id_command_id",
                schema: "serialisation",
                table: "refurbishment_labour",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_operation_tenant_id_command_id",
                schema: "serialisation",
                table: "refurbishment_operation",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_operation_tenant_id_complete_command_id",
                schema: "serialisation",
                table: "refurbishment_operation",
                columns: new[] { "tenant_id", "complete_command_id" },
                unique: true,
                filter: "[complete_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_operation_tenant_id_work_order_id_sequence",
                schema: "serialisation",
                table: "refurbishment_operation",
                columns: new[] { "tenant_id", "work_order_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_part_tenant_id_child_unit_id_recorded_at",
                schema: "serialisation",
                table: "refurbishment_part",
                columns: new[] { "tenant_id", "child_unit_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_part_tenant_id_work_order_id_recorded_at",
                schema: "serialisation",
                table: "refurbishment_part",
                columns: new[] { "tenant_id", "work_order_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_part_tenant_id_command_id",
                schema: "serialisation",
                table: "refurbishment_part",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_work_order_tenant_id_source_rma_line_id",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "source_rma_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_work_order_tenant_id_status_opened_at",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "status", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_work_order_tenant_id_target_unit_id_opened_at",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "target_unit_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_work_order_tenant_id_cancel_command_id",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "cancel_command_id" },
                unique: true,
                filter: "[cancel_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_work_order_tenant_id_open_command_id_target_unit_id",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "open_command_id", "target_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_work_order_tenant_id_release_command_id",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "release_command_id" },
                unique: true,
                filter: "[release_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_refurbishment_work_order_tenant_id_work_order_number",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "work_order_number" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "refurbishment_work_order");
            migrationBuilder.AddTenantPolicy("serialisation", "refurbishment_operation");
            migrationBuilder.AddTenantPolicy("serialisation", "refurbishment_part");
            migrationBuilder.AddTenantPolicy("serialisation", "refurbishment_labour");
            migrationBuilder.AddTenantPolicy("serialisation", "refurbishment_cost");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "refurbishment_cost");
            migrationBuilder.RemoveTenantPolicy("serialisation", "refurbishment_labour");
            migrationBuilder.RemoveTenantPolicy("serialisation", "refurbishment_part");
            migrationBuilder.RemoveTenantPolicy("serialisation", "refurbishment_operation");
            migrationBuilder.RemoveTenantPolicy("serialisation", "refurbishment_work_order");

            migrationBuilder.DropTable(
                name: "refurbishment_cost",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "refurbishment_labour",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "refurbishment_operation",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "refurbishment_part",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "refurbishment_work_order",
                schema: "serialisation");
        }
    }
}
