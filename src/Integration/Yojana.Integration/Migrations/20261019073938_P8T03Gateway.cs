using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T03Gateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canonical_document",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_entity = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    erp_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    message_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    source_inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_canonical_document", x => new { x.tenant_id, x.document_id });
                });

            migrationBuilder.CreateTable(
                name: "connection_capability",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    canonical_message = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    support_level = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_capability", x => new { x.tenant_id, x.connection_id, x.canonical_message });
                    table.CheckConstraint("ck_connection_capability_support", "support_level IN ('native','tenant_code','quantity_only','manual','unsupported')");
                });

            migrationBuilder.CreateTable(
                name: "connection_degradation_setting",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_number = table.Column<int>(type: "int", nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    settings_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_degradation_setting", x => new { x.tenant_id, x.connection_id, x.rule_number });
                    table.CheckConstraint("ck_connection_degradation_rule", "rule_number BETWEEN 1 AND 7");
                });

            migrationBuilder.CreateTable(
                name: "connection_gateway_setting",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ledger_connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    outbound_mode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    native_warranty_object_mode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_gateway_setting", x => new { x.tenant_id, x.connection_id });
                });

            migrationBuilder.CreateTable(
                name: "exception_task",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    exception_task_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    subject_ref = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exception_task", x => new { x.tenant_id, x.exception_task_id });
                });

            migrationBuilder.CreateTable(
                name: "outbound_command",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    outbound_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    module_document_ref = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    erp_reference_text = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_command", x => new { x.tenant_id, x.outbound_command_id });
                });

            migrationBuilder.CreateTable(
                name: "reference_projection",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    module_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    display_name = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    tracking_mode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    uom = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    deleted = table.Column<bool>(type: "bit", nullable: false),
                    provisional = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reference_projection", x => new { x.tenant_id, x.connection_id, x.entity_type, x.external_id });
                });

            migrationBuilder.CreateIndex(
                name: "ux_canonical_document_tenant_id_connection_id_erp_entity_erp_external_id",
                schema: "integration",
                table: "canonical_document",
                columns: new[] { "tenant_id", "connection_id", "erp_entity", "erp_external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exception_task_tenant_id_connection_id_status_created_at_exception_task_id",
                schema: "integration",
                table: "exception_task",
                columns: new[] { "tenant_id", "connection_id", "status", "created_at", "exception_task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_command_tenant_id_connection_id_status_created_at_outbound_command_id",
                schema: "integration",
                table: "outbound_command",
                columns: new[] { "tenant_id", "connection_id", "status", "created_at", "outbound_command_id" });

            migrationBuilder.CreateIndex(
                name: "ux_outbound_command_tenant_id_connection_id_idempotency_key",
                schema: "integration",
                table: "outbound_command",
                columns: new[] { "tenant_id", "connection_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reference_projection_tenant_id_module_id_entity_type",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "module_id", "entity_type" });

            foreach (var table in new[] { "canonical_document", "connection_capability", "connection_degradation_setting",
                         "connection_gateway_setting", "exception_task", "outbound_command", "reference_projection" })
                migrationBuilder.AddTenantPolicy("integration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "canonical_document", "connection_capability", "connection_degradation_setting",
                         "connection_gateway_setting", "exception_task", "outbound_command", "reference_projection" })
                migrationBuilder.RemoveTenantPolicy("integration", table);
            migrationBuilder.DropTable(
                name: "canonical_document",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_capability",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_degradation_setting",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_gateway_setting",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "exception_task",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "outbound_command",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "reference_projection",
                schema: "integration");
        }
    }
}
