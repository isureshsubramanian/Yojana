using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Imports.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T17ImportFileDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityObjects();
            migrationBuilder.EnsureSchema(
                name: "import_files");

            migrationBuilder.CreateTable(
                name: "stored_file",
                schema: "import_files",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    copy_kind = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    content_length = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    stored_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    archived_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_file", x => new { x.tenant_id, x.file_id });
                    table.CheckConstraint("ck_stored_file_kind", "copy_kind IN ('original', 'converted')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_stored_file_tenant_id_archived_at_stored_at",
                schema: "import_files",
                table: "stored_file",
                columns: new[] { "tenant_id", "archived_at", "stored_at" });

            migrationBuilder.CreateIndex(
                name: "ux_stored_file_tenant_id_job_id_copy_kind",
                schema: "import_files",
                table: "stored_file",
                columns: new[] { "tenant_id", "job_id", "copy_kind" },
                unique: true);

            migrationBuilder.AddTenantPolicy("import_files", "stored_file");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("import_files", "stored_file");
            migrationBuilder.DropTable(
                name: "stored_file",
                schema: "import_files");
        }
    }
}
