using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6PerfLedgerReadShapes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledger_base_order_ref",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_time = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    base_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    base_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    base_document_line = table.Column<int>(type: "int", nullable: true),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_base_order_ref", x => new { x.tenant_id, x.event_time, x.event_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_base_order_ref_tenant_id_base_document_type_base_document_id_base_document_line",
                schema: "serialisation",
                table: "ledger_base_order_ref",
                columns: new[] { "tenant_id", "base_document_type", "base_document_id", "base_document_line" })
                .Annotation("SqlServer:Include", new[] { "unit_id" });

            migrationBuilder.AddTenantPolicy("serialisation", "ledger_base_order_ref");

            migrationBuilder.CreateTable(
                name: "ledger_base_order_backfill_checkpoint",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    checkpoint_month = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    newest_month = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    oldest_month = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    cursor_event_time = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    cursor_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    rows_scanned = table.Column<long>(type: "bigint", nullable: false),
                    rows_written = table.Column<long>(type: "bigint", nullable: false),
                    estimated_rows = table.Column<long>(type: "bigint", nullable: true),
                    months_completed = table.Column<int>(type: "int", nullable: false),
                    months_total = table.Column<int>(type: "int", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_base_order_backfill_checkpoint", x => x.tenant_id);
                });

            migrationBuilder.AddTenantPolicy("serialisation", "ledger_base_order_backfill_checkpoint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "ledger_base_order_backfill_checkpoint");
            migrationBuilder.DropTable(
                name: "ledger_base_order_backfill_checkpoint",
                schema: "serialisation");

            migrationBuilder.RemoveTenantPolicy("serialisation", "ledger_base_order_ref");
            migrationBuilder.DropTable(
                name: "ledger_base_order_ref",
                schema: "serialisation");
        }
    }
}
