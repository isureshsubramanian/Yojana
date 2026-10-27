using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Adr25ProductionHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_component_assignment",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    hold_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    component_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    assigned_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_production_component_assignment", x => new { x.tenant_id, x.assignment_id });
                });

            migrationBuilder.CreateTable(
                name: "production_component_hold",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    hold_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    production_order_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    production_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issue_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    issue_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issue_line = table.Column<int>(type: "int", nullable: false),
                    issued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    parent_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    assembly_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    receipt_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    receipt_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    linked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    assigned_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    returned_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_production_component_hold", x => new { x.tenant_id, x.hold_id });
                });

            migrationBuilder.CreateTable(
                name: "production_hold_task",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    task_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    production_order_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    production_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    raised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    completed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_production_hold_task", x => new { x.tenant_id, x.task_id });
                });

            migrationBuilder.CreateTable(
                name: "production_order_receipt",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    production_order_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    production_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    receipt_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    receipt_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    received_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_production_order_receipt", x => new { x.tenant_id, x.parent_unit_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_production_component_assignment_tenant_id_hold_id",
                schema: "serialisation",
                table: "production_component_assignment",
                columns: new[] { "tenant_id", "hold_id" });

            migrationBuilder.CreateIndex(
                name: "ux_production_component_assignment_tenant_id_command_id_component_unit_id",
                schema: "serialisation",
                table: "production_component_assignment",
                columns: new[] { "tenant_id", "command_id", "component_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_component_hold_tenant_id_assembly_link_id",
                schema: "serialisation",
                table: "production_component_hold",
                columns: new[] { "tenant_id", "assembly_link_id" });

            migrationBuilder.CreateIndex(
                name: "ix_production_component_hold_tenant_id_production_order_type_production_order_id_linked_at_returned_at",
                schema: "serialisation",
                table: "production_component_hold",
                columns: new[] { "tenant_id", "production_order_type", "production_order_id", "linked_at", "returned_at" });

            migrationBuilder.CreateIndex(
                name: "ix_production_component_hold_tenant_id_unit_id_linked_at_returned_at",
                schema: "serialisation",
                table: "production_component_hold",
                columns: new[] { "tenant_id", "unit_id", "linked_at", "returned_at" });

            migrationBuilder.CreateIndex(
                name: "ux_production_component_hold_tenant_id_issue_document_id_issue_line_unit_id",
                schema: "serialisation",
                table: "production_component_hold",
                columns: new[] { "tenant_id", "issue_document_id", "issue_line", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_hold_task_tenant_id_production_order_type_production_order_id_kind_status",
                schema: "serialisation",
                table: "production_hold_task",
                columns: new[] { "tenant_id", "production_order_type", "production_order_id", "kind", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_production_hold_task_tenant_id_status_raised_at_task_id",
                schema: "serialisation",
                table: "production_hold_task",
                columns: new[] { "tenant_id", "status", "raised_at", "task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_production_order_receipt_tenant_id_production_order_type_production_order_id_received_at",
                schema: "serialisation",
                table: "production_order_receipt",
                columns: new[] { "tenant_id", "production_order_type", "production_order_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_production_order_receipt_tenant_id_receipt_document_id",
                schema: "serialisation",
                table: "production_order_receipt",
                columns: new[] { "tenant_id", "receipt_document_id" });

            migrationBuilder.AddTenantPolicy("serialisation", "production_component_assignment");
            migrationBuilder.AddTenantPolicy("serialisation", "production_component_hold");
            migrationBuilder.AddTenantPolicy("serialisation", "production_hold_task");
            migrationBuilder.AddTenantPolicy("serialisation", "production_order_receipt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "production_component_assignment");
            migrationBuilder.RemoveTenantPolicy("serialisation", "production_component_hold");
            migrationBuilder.RemoveTenantPolicy("serialisation", "production_hold_task");
            migrationBuilder.RemoveTenantPolicy("serialisation", "production_order_receipt");

            migrationBuilder.DropTable(
                name: "production_component_assignment",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "production_component_hold",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "production_hold_task",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "production_order_receipt",
                schema: "serialisation");
        }
    }
}
