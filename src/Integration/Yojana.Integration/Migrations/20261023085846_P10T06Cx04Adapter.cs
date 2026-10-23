using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T06Cx04Adapter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_health_status",
                schema: "integration",
                table: "connection_health");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_poll_overlap",
                schema: "integration",
                table: "connection_poll_watermark");

            migrationBuilder.CreateTable(
                name: "cx04_authorisation_state",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    pending_state_hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: true),
                    pending_state_expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    refresh_chain_expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    refresh_expiry_mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    refresh_lifetime_days = table.Column<int>(type: "int", nullable: false),
                    reminder14_days_sent = table.Column<bool>(type: "bit", nullable: false),
                    reminder3_days_sent = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cx04_authorisation_state", x => new { x.tenant_id, x.connection_id });
                    table.CheckConstraint("ck_cx04_authorisation_status", "status IN ('reconsent_required','consent_pending','active','disabled')");
                    table.CheckConstraint("ck_cx04_refresh_expiry_mode", "refresh_expiry_mode IN ('absolute','sliding')");
                    table.CheckConstraint("ck_cx04_refresh_lifetime_days", "refresh_lifetime_days BETWEEN 1 AND 365");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_health_status",
                schema: "integration",
                table: "connection_health",
                sql: "status IN ('healthy','degraded','down','paused','archived','reconsent_required','disabled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_poll_overlap",
                schema: "integration",
                table: "connection_poll_watermark",
                sql: "overlap_seconds BETWEEN 0 AND 172800");

            migrationBuilder.CreateIndex(
                name: "ix_cx04_authorisation_state_tenant_id_status_refresh_chain_expires_at_connection_id",
                schema: "integration",
                table: "cx04_authorisation_state",
                columns: new[] { "tenant_id", "status", "refresh_chain_expires_at", "connection_id" });

            migrationBuilder.AddTenantPolicy("integration", "cx04_authorisation_state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "cx04_authorisation_state");

            migrationBuilder.DropTable(
                name: "cx04_authorisation_state",
                schema: "integration");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_health_status",
                schema: "integration",
                table: "connection_health");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_poll_overlap",
                schema: "integration",
                table: "connection_poll_watermark");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_health_status",
                schema: "integration",
                table: "connection_health",
                sql: "status IN ('healthy','degraded','down','paused','archived')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_poll_overlap",
                schema: "integration",
                table: "connection_poll_watermark",
                sql: "overlap_seconds BETWEEN 0 AND 86400");
        }
    }
}
