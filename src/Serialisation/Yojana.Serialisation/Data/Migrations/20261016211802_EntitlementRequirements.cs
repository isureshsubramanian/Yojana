using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntitlementRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entitlement_requirement",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requirement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ownership_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    source_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_document_line = table.Column<int>(type: "int", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    missing_input = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    raised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    waived_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    waiver_reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_requirement", x => new { x.tenant_id, x.requirement_id });
                    table.CheckConstraint("ck_entitlement_requirement_status", "status IN ('open', 'satisfied', 'exception', 'waived')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_requirement_tenant_id_source_document_type_source_document_id_status",
                schema: "serialisation",
                table: "entitlement_requirement",
                columns: new[] { "tenant_id", "source_document_type", "source_document_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_requirement_tenant_id_status_raised_at",
                schema: "serialisation",
                table: "entitlement_requirement",
                columns: new[] { "tenant_id", "status", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ux_entitlement_requirement_tenant_id_unit_id_ownership_episode_id",
                schema: "serialisation",
                table: "entitlement_requirement",
                columns: new[] { "tenant_id", "unit_id", "ownership_episode_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_requirement");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_requirement");

            migrationBuilder.DropTable(
                name: "entitlement_requirement",
                schema: "serialisation");
        }
    }
}
