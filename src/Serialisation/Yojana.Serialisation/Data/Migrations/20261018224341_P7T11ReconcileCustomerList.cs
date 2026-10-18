using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T11ReconcileCustomerList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "evidence_date",
                schema: "serialisation",
                table: "customer_assertion",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ownership_episode_id",
                schema: "serialisation",
                table: "customer_assertion",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "rejected_at",
                schema: "serialisation",
                table: "customer_assertion",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejected_by",
                schema: "serialisation",
                table: "customer_assertion",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                schema: "serialisation",
                table: "customer_assertion",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_reconciliation",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    source_reference_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    total_count = table.Column<int>(type: "int", nullable: false),
                    owned_count = table.Column<int>(type: "int", nullable: false),
                    other_owner_count = table.Column<int>(type: "int", nullable: false),
                    not_found_count = table.Column<int>(type: "int", nullable: false),
                    invalid_count = table.Column<int>(type: "int", nullable: false),
                    reconciled_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    reconciled_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_reconciliation", x => new { x.tenant_id, x.reconciliation_id });
                    table.CheckConstraint("ck_customer_reconciliation_source_kind", "[source_kind] IN ('pasted_list','import_job','selection_set')");
                });

            migrationBuilder.CreateTable(
                name: "customer_reconciliation_row",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconciliation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ordinal = table.Column<int>(type: "int", nullable: false),
                    raw_identifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    requested_identifier_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    matched_identifier_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    normalised_identifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    validation_reasons_json = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    other_owner_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    assertion_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    customer_po_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    customer_item_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_reconciliation_row", x => new { x.tenant_id, x.reconciliation_id, x.ordinal });
                    table.CheckConstraint("ck_customer_reconciliation_row_outcome", "[outcome] IN ('owned','owned_by_another_party','not_found','invalid')");
                    table.CheckConstraint("ck_customer_reconciliation_row_reasons_json", "ISJSON([validation_reasons_json]) = 1");
                    table.ForeignKey(
                        name: "fk_customer_reconciliation_row_tenant_id_assertion_id",
                        columns: x => new { x.tenant_id, x.assertion_id },
                        principalSchema: "serialisation",
                        principalTable: "customer_assertion",
                        principalColumns: new[] { "tenant_id", "assertion_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_reconciliation_row_tenant_id_reconciliation_id",
                        columns: x => new { x.tenant_id, x.reconciliation_id },
                        principalSchema: "serialisation",
                        principalTable: "customer_reconciliation",
                        principalColumns: new[] { "tenant_id", "reconciliation_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_reconciliation_tenant_id_party_ref_reconciled_at_reconciliation_id",
                schema: "serialisation",
                table: "customer_reconciliation",
                columns: new[] { "tenant_id", "party_ref", "reconciled_at", "reconciliation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_reconciliation_row_tenant_id_unit_id",
                schema: "serialisation",
                table: "customer_reconciliation_row",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_reconciliation_row_tenant_id_assertion_id",
                schema: "serialisation",
                table: "customer_reconciliation_row",
                columns: new[] { "tenant_id", "assertion_id" },
                unique: true,
                filter: "[assertion_id] IS NOT NULL");

            migrationBuilder.AddTenantPolicy("serialisation", "customer_reconciliation");
            migrationBuilder.AddTenantPolicy("serialisation", "customer_reconciliation_row");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "customer_reconciliation_row");
            migrationBuilder.RemoveTenantPolicy("serialisation", "customer_reconciliation");

            migrationBuilder.DropTable(
                name: "customer_reconciliation_row",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "customer_reconciliation",
                schema: "serialisation");

            migrationBuilder.DropColumn(
                name: "evidence_date",
                schema: "serialisation",
                table: "customer_assertion");

            migrationBuilder.DropColumn(
                name: "ownership_episode_id",
                schema: "serialisation",
                table: "customer_assertion");

            migrationBuilder.DropColumn(
                name: "rejected_at",
                schema: "serialisation",
                table: "customer_assertion");

            migrationBuilder.DropColumn(
                name: "rejected_by",
                schema: "serialisation",
                table: "customer_assertion");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                schema: "serialisation",
                table: "customer_assertion");
        }
    }
}
