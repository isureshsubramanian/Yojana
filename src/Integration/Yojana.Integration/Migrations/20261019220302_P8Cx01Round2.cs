using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8Cx01Round2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_notification_receipt",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    first_seen_pending_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    first_seen_pending_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    last_observed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    processed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_notification_receipt", x => new { x.tenant_id, x.connection_id, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "connection_notification_watchdog",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    watermark_date = table.Column<DateOnly>(type: "date", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_notification_watchdog", x => new { x.tenant_id, x.connection_id });
                });

            migrationBuilder.CreateTable(
                name: "cx01_survey",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    observed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    result_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cx01_survey", x => new { x.tenant_id, x.connection_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_connection_notification_receipt_tenant_id_connection_id_first_seen_pending_at_event_id",
                schema: "integration",
                table: "connection_notification_receipt",
                columns: new[] { "tenant_id", "connection_id", "first_seen_pending_at", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_notification_receipt_tenant_id_processed_at_connection_id_event_id",
                schema: "integration",
                table: "connection_notification_receipt",
                columns: new[] { "tenant_id", "processed_at", "connection_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_notification_watchdog_tenant_id_updated_at_connection_id",
                schema: "integration",
                table: "connection_notification_watchdog",
                columns: new[] { "tenant_id", "updated_at", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cx01_survey_tenant_id_observed_at_connection_id",
                schema: "integration",
                table: "cx01_survey",
                columns: new[] { "tenant_id", "observed_at", "connection_id" });

            foreach (var table in new[]
                     {
                         "connection_notification_receipt", "connection_notification_watchdog", "cx01_survey",
                     })
                migrationBuilder.AddTenantPolicy("integration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
                     {
                         "connection_notification_receipt", "connection_notification_watchdog", "cx01_survey",
                     })
                migrationBuilder.RemoveTenantPolicy("integration", table);

            migrationBuilder.DropTable(
                name: "connection_notification_receipt",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_notification_watchdog",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "cx01_survey",
                schema: "integration");
        }
    }
}
