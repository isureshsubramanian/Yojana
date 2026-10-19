using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T04InboundInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbound_message",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    erp_entity = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    erp_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    erp_change_marker = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    marker_kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    contract_version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    message_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    raw_payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    canonical_message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    observed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    is_latest = table.Column<bool>(type: "bit", nullable: false),
                    supersedes_inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    translation_status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    translated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_message", x => new { x.tenant_id, x.inbound_message_id });
                    table.CheckConstraint("ck_inbound_message_translation_status", "translation_status IN ('pending','superseded','completed','late')");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_index",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_entity = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    erp_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    erp_change_marker = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claimed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_index", x => new { x.tenant_id, x.connection_id, x.erp_entity, x.erp_external_id, x.erp_change_marker });
                    table.ForeignKey(
                        name: "fk_idempotency_index_tenant_id_inbound_message_id",
                        columns: x => new { x.tenant_id, x.inbound_message_id },
                        principalSchema: "integration",
                        principalTable: "inbound_message",
                        principalColumns: new[] { "tenant_id", "inbound_message_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_index_tenant_id_inbound_message_id",
                schema: "integration",
                table: "idempotency_index",
                columns: new[] { "tenant_id", "inbound_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbound_message_tenant_id_translation_status_accepted_at_inbound_message_id",
                schema: "integration",
                table: "inbound_message",
                columns: new[] { "tenant_id", "translation_status", "accepted_at", "inbound_message_id" });

            migrationBuilder.CreateIndex(
                name: "ux_inbound_message_tenant_id_connection_id_erp_entity_erp_external_id_is_latest",
                schema: "integration",
                table: "inbound_message",
                columns: new[] { "tenant_id", "connection_id", "erp_entity", "erp_external_id", "is_latest" },
                unique: true,
                filter: "[is_latest] = 1");

            migrationBuilder.AddTenantPolicy("integration", "inbound_message");
            migrationBuilder.AddTenantPolicy("integration", "idempotency_index");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "idempotency_index");
            migrationBuilder.RemoveTenantPolicy("integration", "inbound_message");
            migrationBuilder.DropTable(
                name: "idempotency_index",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "inbound_message",
                schema: "integration");
        }
    }
}
