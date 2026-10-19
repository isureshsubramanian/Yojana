using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T08DeadLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dead_letter_item",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dead_letter_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    record_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reason_code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    raw_payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    canonical_attempt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    failure = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dead_letter_item", x => new { x.tenant_id, x.dead_letter_item_id });
                    table.CheckConstraint("ck_dead_letter_direction", "direction IN ('inbound','outbound')");
                    table.CheckConstraint("ck_dead_letter_status", "status IN ('needs_attention','retry_pending','resolved','discarded')");
                });

            migrationBuilder.CreateTable(
                name: "dead_letter_event",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dead_letter_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dead_letter_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    action = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    details_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dead_letter_event", x => new { x.tenant_id, x.dead_letter_event_id });
                    table.ForeignKey(
                        name: "fk_dead_letter_event_tenant_id_dead_letter_item_id",
                        columns: x => new { x.tenant_id, x.dead_letter_item_id },
                        principalSchema: "integration",
                        principalTable: "dead_letter_item",
                        principalColumns: new[] { "tenant_id", "dead_letter_item_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "manual_outbound_task",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    manual_outbound_task_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dead_letter_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    outbound_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    draft = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    erp_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    erp_change_marker = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    completed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_outbound_task", x => new { x.tenant_id, x.manual_outbound_task_id });
                    table.CheckConstraint("ck_manual_outbound_task_status", "status IN ('open','completed')");
                    table.ForeignKey(
                        name: "fk_manual_outbound_task_tenant_id_dead_letter_item_id",
                        columns: x => new { x.tenant_id, x.dead_letter_item_id },
                        principalSchema: "integration",
                        principalTable: "dead_letter_item",
                        principalColumns: new[] { "tenant_id", "dead_letter_item_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_event_tenant_id_dead_letter_item_id_occurred_at_dead_letter_event_id",
                schema: "integration",
                table: "dead_letter_event",
                columns: new[] { "tenant_id", "dead_letter_item_id", "occurred_at", "dead_letter_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_item_tenant_id_connection_id_reason_code_record_type_created_at_dead_letter_item_id",
                schema: "integration",
                table: "dead_letter_item",
                columns: new[] { "tenant_id", "connection_id", "reason_code", "record_type", "created_at", "dead_letter_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_item_tenant_id_status_created_at_dead_letter_item_id",
                schema: "integration",
                table: "dead_letter_item",
                columns: new[] { "tenant_id", "status", "created_at", "dead_letter_item_id" });

            migrationBuilder.CreateIndex(
                name: "ux_dead_letter_item_tenant_id_source_kind_source_id",
                schema: "integration",
                table: "dead_letter_item",
                columns: new[] { "tenant_id", "source_kind", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_manual_outbound_task_tenant_id_status_created_at_manual_outbound_task_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "status", "created_at", "manual_outbound_task_id" });

            migrationBuilder.CreateIndex(
                name: "ux_manual_outbound_task_tenant_id_dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "dead_letter_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_manual_outbound_task_tenant_id_outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "outbound_command_id" },
                unique: true);

            foreach (var table in new[] { "dead_letter_item", "dead_letter_event", "manual_outbound_task" })
                migrationBuilder.AddTenantPolicy("integration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "manual_outbound_task", "dead_letter_event", "dead_letter_item" })
                migrationBuilder.RemoveTenantPolicy("integration", table);
            migrationBuilder.DropTable(
                name: "dead_letter_event",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "manual_outbound_task",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "dead_letter_item",
                schema: "integration");
        }
    }
}
