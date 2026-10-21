using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T15TemplateDecomposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "template_mapping",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_mapping_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    template_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    template_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    extraction_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    source_fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    source_terms_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    wording_to_review_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    pattern_key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    pattern_label = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    decomposition_fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    needs_person = table.Column<bool>(type: "bit", nullable: false),
                    review_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    contract_count = table.Column<int>(type: "int", nullable: false),
                    serial_count = table.Column<int>(type: "int", nullable: false),
                    active_contract_count = table.Column<int>(type: "int", nullable: false),
                    active_serial_count = table.Column<int>(type: "int", nullable: false),
                    proposed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    approved_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    approved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    superseded_by_mapping_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    superseded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_mapping", x => new { x.tenant_id, x.template_mapping_id });
                    table.CheckConstraint("ck_template_mapping_state", "state IN ('draft','approved','superseded')");
                });

            migrationBuilder.CreateTable(
                name: "template_mapping_block",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_mapping_block_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_mapping_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    position = table.Column<int>(type: "int", nullable: false),
                    family_code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    family_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    stacking = table.Column<bool>(type: "bit", nullable: false),
                    resolution = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    policy_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    policy_code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    policy_version = table.Column<int>(type: "int", nullable: true),
                    policy_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    proposed_terms_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_mapping_block", x => new { x.tenant_id, x.template_mapping_block_id });
                    table.CheckConstraint("ck_template_mapping_block_resolution", "resolution IN ('existing_policy','policy_to_create')");
                    table.ForeignKey(
                        name: "fk_template_mapping_block_tenant_id_template_mapping_id",
                        columns: x => new { x.tenant_id, x.template_mapping_id },
                        principalSchema: "migration",
                        principalTable: "template_mapping",
                        principalColumns: new[] { "tenant_id", "template_mapping_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_template_mapping_tenant_id_pattern_key_state_template_key",
                schema: "migration",
                table: "template_mapping",
                columns: new[] { "tenant_id", "pattern_key", "state", "template_key" });

            migrationBuilder.CreateIndex(
                name: "ix_template_mapping_tenant_id_template_key_state",
                schema: "migration",
                table: "template_mapping",
                columns: new[] { "tenant_id", "template_key", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_template_mapping_tenant_id_template_key",
                schema: "migration",
                table: "template_mapping",
                columns: new[] { "tenant_id", "template_key" },
                unique: true,
                filter: "[state] <> 'superseded'");

            migrationBuilder.CreateIndex(
                name: "ux_template_mapping_block_tenant_id_template_mapping_id_family_code",
                schema: "migration",
                table: "template_mapping_block",
                columns: new[] { "tenant_id", "template_mapping_id", "family_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_template_mapping_block_tenant_id_template_mapping_id_position",
                schema: "migration",
                table: "template_mapping_block",
                columns: new[] { "tenant_id", "template_mapping_id", "position" },
                unique: true);

            migrationBuilder.AddTenantPolicy("migration", "template_mapping");
            migrationBuilder.AddTenantPolicy("migration", "template_mapping_block");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "template_mapping_block");
            migrationBuilder.RemoveTenantPolicy("migration", "template_mapping");

            migrationBuilder.DropTable(
                name: "template_mapping_block",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "template_mapping",
                schema: "migration");
        }
    }
}
