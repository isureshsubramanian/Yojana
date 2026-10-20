using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T23SiteRelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "relay_connected",
                schema: "integration",
                table: "connection_health",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "relay",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    relay_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    agent_version = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    erp_version = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    probed_erp_version = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    host_name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    signing_key_envelope = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    previous_signing_key_envelope = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relay", x => new { x.tenant_id, x.relay_id });
                    table.CheckConstraint("ck_relay_status", "status IN ('active','revoked')");
                    table.ForeignKey(
                        name: "fk_relay_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalSchema: "integration",
                        principalTable: "connection",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "relay_nonce",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    relay_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    nonce = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relay_nonce", x => new { x.tenant_id, x.relay_id, x.nonce });
                });

            migrationBuilder.CreateTable(
                name: "relay_request",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    relay_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lease_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    payload_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    leased_until = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    result_json = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relay_request", x => new { x.tenant_id, x.request_id });
                    table.CheckConstraint("ck_relay_request_payload", "DATALENGTH(payload_json) <= 25165824");
                    table.CheckConstraint("ck_relay_request_result", "DATALENGTH(result_json) <= 25165824");
                    table.ForeignKey(
                        name: "fk_relay_request_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalSchema: "integration",
                        principalTable: "connection",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_relay_tenant_id_connection_id_status_last_seen_at_relay_id",
                schema: "integration",
                table: "relay",
                columns: new[] { "tenant_id", "connection_id", "status", "last_seen_at", "relay_id" });

            migrationBuilder.CreateIndex(
                name: "ix_relay_nonce_tenant_id_expires_at",
                schema: "integration",
                table: "relay_nonce",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_relay_nonce_tenant_id_relay_id_seen_at",
                schema: "integration",
                table: "relay_nonce",
                columns: new[] { "tenant_id", "relay_id", "seen_at" });

            migrationBuilder.CreateIndex(
                name: "ix_relay_request_tenant_id_connection_id_lease_id_created_at_request_id",
                schema: "integration",
                table: "relay_request",
                columns: new[] { "tenant_id", "connection_id", "lease_id", "created_at", "request_id" });

            migrationBuilder.CreateIndex(
                name: "ix_relay_request_tenant_id_expires_at_request_id",
                schema: "integration",
                table: "relay_request",
                columns: new[] { "tenant_id", "expires_at", "request_id" });
            migrationBuilder.AddTenantPolicy("integration", "relay");
            migrationBuilder.AddTenantPolicy("integration", "relay_request");
            migrationBuilder.AddTenantPolicy("integration", "relay_nonce");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "relay");
            migrationBuilder.RemoveTenantPolicy("integration", "relay_request");
            migrationBuilder.RemoveTenantPolicy("integration", "relay_nonce");
            migrationBuilder.DropTable(
                name: "relay",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "relay_nonce",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "relay_request",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "relay_connected",
                schema: "integration",
                table: "connection_health");
        }
    }
}
