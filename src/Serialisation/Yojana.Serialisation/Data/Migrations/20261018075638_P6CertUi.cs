using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6CertUi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the earlier free-form reference while widening it for the issuer-to-asset JSON map. Existing
            // values remain readable; new template drafts write only the narrow map contract.
            migrationBuilder.AlterColumn<string>(
                name: "branding_asset_ref",
                schema: "serialisation",
                table: "certificate_template_version",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "branding_asset_id",
                schema: "serialisation",
                table: "certificate_template_version",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_asset",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    asset_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    media_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "int", nullable: false),
                    height = table.Column<int>(type: "int", nullable: false),
                    uploaded_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_asset", x => new { x.tenant_id, x.asset_id });
                    table.CheckConstraint("ck_tenant_asset_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_tenant_asset_kind", "kind = 'logo'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_template_version_tenant_id_branding_asset_id",
                schema: "serialisation",
                table: "certificate_template_version",
                columns: new[] { "tenant_id", "branding_asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_asset_tenant_id_kind_legal_entity_id_uploaded_at",
                schema: "serialisation",
                table: "tenant_asset",
                columns: new[] { "tenant_id", "kind", "legal_entity_id", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_asset_tenant_id_sha256",
                schema: "serialisation",
                table: "tenant_asset",
                columns: new[] { "tenant_id", "sha256" });

            migrationBuilder.AddForeignKey(
                name: "fk_certificate_template_version_tenant_id_branding_asset_id",
                schema: "serialisation",
                table: "certificate_template_version",
                columns: new[] { "tenant_id", "branding_asset_id" },
                principalSchema: "serialisation",
                principalTable: "tenant_asset",
                principalColumns: new[] { "tenant_id", "asset_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddTenantPolicy("serialisation", "tenant_asset");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "tenant_asset");

            migrationBuilder.DropForeignKey(
                name: "fk_certificate_template_version_tenant_id_branding_asset_id",
                schema: "serialisation",
                table: "certificate_template_version");

            migrationBuilder.DropTable(
                name: "tenant_asset",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_certificate_template_version_tenant_id_branding_asset_id",
                schema: "serialisation",
                table: "certificate_template_version");

            migrationBuilder.DropColumn(
                name: "branding_asset_id",
                schema: "serialisation",
                table: "certificate_template_version");

            migrationBuilder.AlterColumn<string>(
                name: "branding_asset_ref",
                schema: "serialisation",
                table: "certificate_template_version",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

        }
    }
}
