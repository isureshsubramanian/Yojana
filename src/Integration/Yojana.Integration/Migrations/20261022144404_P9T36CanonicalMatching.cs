using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T36CanonicalMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_family_normalised_serial",
                schema: "migration",
                table: "migration_unit_candidate",
                columns: new[] { "tenant_id", "run_id", "item_code", "family_normalised_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_stored_serial",
                schema: "migration",
                table: "migration_unit_candidate",
                columns: new[] { "tenant_id", "run_id", "item_code", "stored_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_trimmed_serial",
                schema: "migration",
                table: "migration_unit_candidate",
                columns: new[] { "tenant_id", "run_id", "item_code", "trimmed_serial" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_family_normalised_serial",
                schema: "migration",
                table: "migration_unit_candidate");

            migrationBuilder.DropIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_stored_serial",
                schema: "migration",
                table: "migration_unit_candidate");

            migrationBuilder.DropIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_trimmed_serial",
                schema: "migration",
                table: "migration_unit_candidate");
        }
    }
}
