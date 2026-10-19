using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T10Backfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "capture_mode",
                schema: "integration",
                table: "inbound_message",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "observed");

            migrationBuilder.CreateTable(
                name: "backfill_cursor",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    backfill_cursor_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    range_start = table.Column<DateOnly>(type: "date", nullable: false),
                    range_end = table.Column<DateOnly>(type: "date", nullable: false),
                    cursor = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    page_size = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    requested_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    last_changed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    requested_by_reconciliation_result_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    processed_messages = table.Column<long>(type: "bigint", nullable: false),
                    processed_pages = table.Column<int>(type: "int", nullable: false),
                    total_messages = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_page_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    awaiting_reconciliation_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    completion_reconciliation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_failure = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backfill_cursor", x => new { x.tenant_id, x.backfill_cursor_id });
                    table.CheckConstraint("ck_backfill_cursor_status", "status IN ('queued','running','paused','awaiting_reconciliation','completed','cancelled')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_backfill_cursor_tenant_id_connection_id_entity_status_created_at_backfill_cursor_id",
                schema: "integration",
                table: "backfill_cursor",
                columns: new[] { "tenant_id", "connection_id", "entity", "status", "created_at", "backfill_cursor_id" });

            migrationBuilder.CreateIndex(
                name: "ix_backfill_cursor_tenant_id_created_at_backfill_cursor_id",
                schema: "integration",
                table: "backfill_cursor",
                columns: new[] { "tenant_id", "created_at", "backfill_cursor_id" });

            migrationBuilder.AddTenantPolicy("integration", "backfill_cursor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "backfill_cursor");
            migrationBuilder.DropTable(
                name: "backfill_cursor",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "capture_mode",
                schema: "integration",
                table: "inbound_message");
        }
    }
}
