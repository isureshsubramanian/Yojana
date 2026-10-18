using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T02Portal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_assertion",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    assertion_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    identifier_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    raw_identifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    normalised_identifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    customer_po_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    customer_item_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    evidence_ref = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    asserted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    asserted_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    verified_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    verified_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_assertion", x => new { x.tenant_id, x.assertion_id });
                    table.CheckConstraint("ck_customer_assertion_status", "[status] IN ('pending_verification','verified','rejected')");
                });

            migrationBuilder.CreateTable(
                name: "disclosure_policy",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    field = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    tier = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disclosure_policy", x => new { x.tenant_id, x.field });
                    table.CheckConstraint("ck_disclosure_policy_tier", "[tier] IN ('public','owner','internal')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_assertion_tenant_id_normalised_identifier_identifier_type",
                schema: "serialisation",
                table: "customer_assertion",
                columns: new[] { "tenant_id", "normalised_identifier", "identifier_type" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_assertion_tenant_id_party_ref_asserted_at_assertion_id",
                schema: "serialisation",
                table: "customer_assertion",
                columns: new[] { "tenant_id", "party_ref", "asserted_at", "assertion_id" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_assertion_tenant_id_command_id",
                schema: "serialisation",
                table: "customer_assertion",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_disclosure_policy_tenant_id_tier_enabled",
                schema: "serialisation",
                table: "disclosure_policy",
                columns: new[] { "tenant_id", "tier", "enabled" });

            migrationBuilder.AddTenantPolicy("serialisation", "customer_assertion");
            migrationBuilder.AddTenantPolicy("serialisation", "disclosure_policy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "disclosure_policy");
            migrationBuilder.RemoveTenantPolicy("serialisation", "customer_assertion");

            migrationBuilder.DropTable(
                name: "customer_assertion",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "disclosure_policy",
                schema: "serialisation");
        }
    }
}
