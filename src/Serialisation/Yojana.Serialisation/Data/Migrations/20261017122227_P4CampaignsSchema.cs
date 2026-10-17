using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4CampaignsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaign",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    coverage_source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    reserve_tag = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    selected_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign", x => new { x.tenant_id, x.campaign_id });
                    table.CheckConstraint("ck_campaign_coverage_source", "coverage_source IN ('recall', 'goodwill')");
                    table.CheckConstraint("ck_campaign_status", "status IN ('draft', 'selected', 'active', 'closed')");
                });

            migrationBuilder.CreateTable(
                name: "campaign_criteria",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    scheme = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    identifier_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    range_start = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    range_end = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    range_start_ordinal = table.Column<long>(type: "bigint", nullable: true),
                    range_end_ordinal = table.Column<long>(type: "bigint", nullable: true),
                    lot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    child_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    child_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    firmware_value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    effective_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_criteria", x => new { x.tenant_id, x.criterion_id });
                    table.CheckConstraint("ck_campaign_criteria_shape", "(kind = 'identifier_range' AND scheme IS NOT NULL AND identifier_type IS NOT NULL AND range_start IS NOT NULL AND range_end IS NOT NULL AND range_start_ordinal IS NOT NULL AND range_end_ordinal IS NOT NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'lot' AND lot_id IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'child' AND ((child_unit_id IS NOT NULL AND child_item_id IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL)) AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'firmware' AND firmware_value IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_campaign_criteria_tenant_id_campaign_id",
                        columns: x => new { x.tenant_id, x.campaign_id },
                        principalSchema: "serialisation",
                        principalTable: "campaign",
                        principalColumns: new[] { "tenant_id", "campaign_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaign_unit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    owner_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    owner_party_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    custodian_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    custodian_party_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    selected_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    selected_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    selection_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_unit", x => new { x.tenant_id, x.campaign_id, x.unit_id });
                    table.ForeignKey(
                        name: "fk_campaign_unit_tenant_id_campaign_id",
                        columns: x => new { x.tenant_id, x.campaign_id },
                        principalSchema: "serialisation",
                        principalTable: "campaign",
                        principalColumns: new[] { "tenant_id", "campaign_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_tenant_id_status_updated_at",
                schema: "serialisation",
                table: "campaign",
                columns: new[] { "tenant_id", "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_campaign_tenant_id_code",
                schema: "serialisation",
                table: "campaign",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_tenant_id_campaign_id_kind",
                schema: "serialisation",
                table: "campaign_criteria",
                columns: new[] { "tenant_id", "campaign_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_tenant_id_child_item_id_campaign_id",
                schema: "serialisation",
                table: "campaign_criteria",
                columns: new[] { "tenant_id", "child_item_id", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_tenant_id_child_unit_id_campaign_id",
                schema: "serialisation",
                table: "campaign_criteria",
                columns: new[] { "tenant_id", "child_unit_id", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_criteria_tenant_id_lot_id_campaign_id",
                schema: "serialisation",
                table: "campaign_criteria",
                columns: new[] { "tenant_id", "lot_id", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_unit_tenant_id_campaign_id_selected_at",
                schema: "serialisation",
                table: "campaign_unit",
                columns: new[] { "tenant_id", "campaign_id", "selected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_unit_tenant_id_custodian_party_ref_campaign_id",
                schema: "serialisation",
                table: "campaign_unit",
                columns: new[] { "tenant_id", "custodian_party_ref", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_unit_tenant_id_owner_party_ref_campaign_id",
                schema: "serialisation",
                table: "campaign_unit",
                columns: new[] { "tenant_id", "owner_party_ref", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_unit_tenant_id_selection_command_id",
                schema: "serialisation",
                table: "campaign_unit",
                columns: new[] { "tenant_id", "selection_command_id" });

            migrationBuilder.AddTenantPolicy("serialisation", "campaign");
            migrationBuilder.AddTenantPolicy("serialisation", "campaign_criteria");
            migrationBuilder.AddTenantPolicy("serialisation", "campaign_unit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "campaign_unit");
            migrationBuilder.RemoveTenantPolicy("serialisation", "campaign_criteria");
            migrationBuilder.RemoveTenantPolicy("serialisation", "campaign");

            migrationBuilder.DropTable(
                name: "campaign_criteria",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "campaign_unit",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "campaign",
                schema: "serialisation");
        }
    }
}
