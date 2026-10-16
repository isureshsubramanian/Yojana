using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <summary>
    /// Stage table for the second archival of a month (ADR-0001 "Scale and storage", amendment 2026-10-15): same
    /// columns, scheme, indexes and security policy as the two ledger tables, so SWITCH works in both directions.
    /// Partitioned, so it is T-SQL through LedgerDdl like the other two.
    /// </summary>
    public partial class ArchiveStage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            LedgerDdl.CreateLedgerTable(migrationBuilder, "unit_event_archive_stage", "ux_unit_event_archive_stage_tenant_id_idempotency_key_event_time", "ix_unit_event_archive_stage_tenant_id_unit_id_event_time");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_event_archive_stage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_event_archive_stage");
            migrationBuilder.DropTable(
                name: "unit_event_archive_stage",
                schema: "serialisation");
        }
    }
}
