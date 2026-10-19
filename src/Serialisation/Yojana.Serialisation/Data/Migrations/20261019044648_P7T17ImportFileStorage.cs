using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T17ImportFileStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "converted_archived",
                schema: "serialisation",
                table: "import_job",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "converted_file_reference",
                schema: "serialisation",
                table: "import_job",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "converted_length",
                schema: "serialisation",
                table: "import_job",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "original_archived",
                schema: "serialisation",
                table: "import_job",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "original_file_reference",
                schema: "serialisation",
                table: "import_job",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "original_length",
                schema: "serialisation",
                table: "import_job",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_file_download_audit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    audit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    copy_kind = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    downloaded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_file_download_audit", x => new { x.tenant_id, x.audit_id });
                    table.CheckConstraint("ck_import_file_download_audit_kind", "copy_kind IN ('original', 'converted')");
                    table.ForeignKey(
                        name: "fk_import_file_download_audit_tenant_id_job_id",
                        columns: x => new { x.tenant_id, x.job_id },
                        principalSchema: "serialisation",
                        principalTable: "import_job",
                        principalColumns: new[] { "tenant_id", "job_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_recorded_time_job_id",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "recorded_time", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_file_download_audit_tenant_id_job_id_downloaded_at",
                schema: "serialisation",
                table: "import_file_download_audit",
                columns: new[] { "tenant_id", "job_id", "downloaded_at" });

            migrationBuilder.AddTenantPolicy("serialisation", "import_file_download_audit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "import_file_download_audit");
            migrationBuilder.DropTable(
                name: "import_file_download_audit",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_import_job_tenant_id_recorded_time_job_id",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "converted_archived",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "converted_file_reference",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "converted_length",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "original_archived",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "original_file_reference",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "original_length",
                schema: "serialisation",
                table: "import_job");
        }
    }
}
