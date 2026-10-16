using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntitlementGapReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_unit_id_coverage_family_status_closed_reason",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "unit_id", "coverage_family", "status", "closed_reason" });

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_kind_party_type_valid_from",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "kind", "party_type", "valid_from" })
                .Annotation("SqlServer:Include", new[] { "unit_id", "episode_id", "party_ref", "source", "opening_document_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entitlement_tenant_id_unit_id_coverage_family_status_closed_reason",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropIndex(
                name: "ix_custody_episode_tenant_id_kind_party_type_valid_from",
                schema: "serialisation",
                table: "custody_episode");
        }
    }
}
