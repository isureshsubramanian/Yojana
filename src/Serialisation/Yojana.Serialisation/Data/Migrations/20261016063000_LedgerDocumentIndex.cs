using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <summary>Events by document on the three ledger tables (P1-T21 finding, see <see cref="LedgerDdl.CreateDocumentIndex"/>). Raw DDL only; the model is unchanged.</summary>
    [DbContext(typeof(SerialisationDbContext))]
    [Migration("20261016063000_LedgerDocumentIndex")]
    public partial class LedgerDocumentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            LedgerDdl.CreateDocumentIndex(migrationBuilder, "unit_event", "ix_unit_event_tenant_id_document");
            LedgerDdl.CreateDocumentIndex(migrationBuilder, "unit_event_archive", "ix_unit_event_archive_tenant_id_document");
            LedgerDdl.CreateDocumentIndex(migrationBuilder, "unit_event_archive_stage", "ix_unit_event_archive_stage_tenant_id_document");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            LedgerDdl.DropDocumentIndex(migrationBuilder, "unit_event_archive_stage", "ix_unit_event_archive_stage_tenant_id_document");
            LedgerDdl.DropDocumentIndex(migrationBuilder, "unit_event_archive", "ix_unit_event_archive_tenant_id_document");
            LedgerDdl.DropDocumentIndex(migrationBuilder, "unit_event", "ix_unit_event_tenant_id_document");
        }
    }
}
