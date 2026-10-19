using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T03ImportReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_import_job_tenant_id_source_original_hash",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_import_job_status",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.CreateIndex(
                name: "ux_import_job_tenant_id_source_original_hash_mode_target_type_target_id_target_line",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "source", "original_hash", "mode", "target_type", "target_id", "target_line" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_job_status",
                schema: "serialisation",
                table: "import_job",
                sql: "status IN ('intake', 'compared', 'reviewed', 'handed_off', 'applied', 'partial', 'rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_import_job_tenant_id_source_original_hash_mode_target_type_target_id_target_line",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_import_job_status",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_source_original_hash",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "source", "original_hash" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_job_status",
                schema: "serialisation",
                table: "import_job",
                sql: "status IN ('intake', 'applied', 'partial', 'rejected', 'compared')");
        }
    }
}
