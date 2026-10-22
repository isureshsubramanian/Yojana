using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P13T12RetentionFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityObjects();
            migrationBuilder.EnableReadCommittedSnapshot();

            migrationBuilder.EnsureSchema(
                name: "platform_files");

            migrationBuilder.CreateTable(
                name: "stored_file",
                schema: "platform_files",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_kind = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    content_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    content_length = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_file", x => new { x.tenant_id, x.file_id });
                });

            migrationBuilder.AddTenantPolicy("platform_files", "stored_file");

            migrationBuilder.CreateIndex(
                name: "ix_stored_file_tenant_id_record_kind_record_id_uploaded_at_file_id",
                schema: "platform_files",
                table: "stored_file",
                columns: new[] { "tenant_id", "record_kind", "record_id", "uploaded_at", "file_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stored_file_tenant_id_sha256_file_id",
                schema: "platform_files",
                table: "stored_file",
                columns: new[] { "tenant_id", "sha256", "file_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("platform_files", "stored_file");

            migrationBuilder.DropTable(
                name: "stored_file",
                schema: "platform_files");
        }
    }
}
