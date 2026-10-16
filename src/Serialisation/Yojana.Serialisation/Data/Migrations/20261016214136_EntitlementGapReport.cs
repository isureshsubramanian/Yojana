using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntitlementGapReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entitlement_gap_run",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    run_trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    filters_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    total = table.Column<int>(type: "int", nullable: false),
                    counts_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_gap_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_entitlement_gap_run_counts_json", "ISJSON(counts_json) = 1");
                    table.CheckConstraint("ck_entitlement_gap_run_filters_json", "ISJSON(filters_json) = 1");
                    table.CheckConstraint("ck_entitlement_gap_run_trigger", "run_trigger IN ('migration_load', 'on_demand', 'daily_job')");
                });

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

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_gap_run_tenant_id_run_at",
                schema: "serialisation",
                table: "entitlement_gap_run",
                columns: new[] { "tenant_id", "run_at" });

            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_gap_run");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_gap_run");

            migrationBuilder.DropTable(
                name: "entitlement_gap_run",
                schema: "serialisation");

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
