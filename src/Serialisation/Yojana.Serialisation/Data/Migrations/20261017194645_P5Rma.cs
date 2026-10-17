using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5Rma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "return_reason",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    restock_fee_rule = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    restock_fee_value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    doa_window_days = table.Column<int>(type: "int", nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_reason", x => new { x.tenant_id, x.code });
                    table.CheckConstraint("ck_return_reason_doa_window", "doa_window_days IS NULL OR doa_window_days >= 0");
                    table.CheckConstraint("ck_return_reason_restock_fee", "restock_fee_rule IN ('none', 'fixed', 'percentage') AND (restock_fee_value IS NULL OR restock_fee_value >= 0)");
                });

            migrationBuilder.CreateTable(
                name: "rma",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rma_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rma_number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    stage = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    request_date = table.Column<DateOnly>(type: "date", nullable: false),
                    returning_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    settlement_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    obligor_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_country = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    authorised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    in_transit_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    received_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    dispositioned_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    requested_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    last_actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    last_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rma", x => new { x.tenant_id, x.rma_id });
                    table.CheckConstraint("ck_rma_stage", "stage IN ('requested', 'authorised', 'in_transit', 'received', 'dispositioned', 'closed')");
                });

            migrationBuilder.CreateTable(
                name: "rma_line",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rma_line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rma_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    requested_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    authorised_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ownership_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    original_sale_document_type = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    original_sale_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    original_sale_document_line = table.Column<int>(type: "int", nullable: true),
                    parent_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    price_allocation = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    parent_incomplete = table.Column<bool>(type: "bit", nullable: false),
                    coverage_family = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    claim_line_kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    requested_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    coverage_pct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    approved_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    coverage_source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    policy_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    denial_reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    received_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    provisional_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    receipt_outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    condition_grade = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    valuation_rule = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    disposition = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    replacement_intent_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    entitlement_closed_reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rma_line", x => new { x.tenant_id, x.rma_line_id });
                    table.CheckConstraint("ck_rma_line_amounts", "requested_amount >= 0 AND (coverage_pct IS NULL OR coverage_pct BETWEEN 0 AND 100) AND (approved_amount IS NULL OR (approved_amount >= 0 AND approved_amount <= requested_amount))");
                    table.CheckConstraint("ck_rma_line_disposition", "disposition IS NULL OR disposition IN ('return_to_stock', 'refurbish', 'repair_and_return', 'scrap', 'return_to_vendor', 'credit', 'refund', 'replace', 'reject', 'restock_as_new')");
                    table.CheckConstraint("ck_rma_line_outcome", "receipt_outcome IS NULL OR receipt_outcome IN ('accepted', 'mismatch', 'unauthorised', 'counterfeit')");
                    table.ForeignKey(
                        name: "fk_rma_line_tenant_id_rma_id",
                        columns: x => new { x.tenant_id, x.rma_id },
                        principalSchema: "serialisation",
                        principalTable: "rma",
                        principalColumns: new[] { "tenant_id", "rma_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "return_receipt",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    return_receipt_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rma_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rma_line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    authorised_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    received_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    provisional_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    condition_grade = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    valuation_rule = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    exception_note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    receiving_location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    business_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    posting_period = table.Column<string>(type: "char(7)", unicode: false, fixedLength: true, maxLength: 7, nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    received_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_receipt", x => new { x.tenant_id, x.return_receipt_id });
                    table.CheckConstraint("ck_return_receipt_identity", "(received_unit_id IS NOT NULL AND provisional_unit_id IS NULL) OR (received_unit_id IS NULL AND provisional_unit_id IS NOT NULL)");
                    table.CheckConstraint("ck_return_receipt_outcome", "outcome IN ('accepted', 'mismatch', 'unauthorised', 'counterfeit')");
                    table.ForeignKey(
                        name: "fk_return_receipt_tenant_id_rma_id",
                        columns: x => new { x.tenant_id, x.rma_id },
                        principalSchema: "serialisation",
                        principalTable: "rma",
                        principalColumns: new[] { "tenant_id", "rma_id" });
                    table.ForeignKey(
                        name: "fk_return_receipt_tenant_id_rma_line_id",
                        columns: x => new { x.tenant_id, x.rma_line_id },
                        principalSchema: "serialisation",
                        principalTable: "rma_line",
                        principalColumns: new[] { "tenant_id", "rma_line_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_return_reason_tenant_id_active_name",
                schema: "serialisation",
                table: "return_reason",
                columns: new[] { "tenant_id", "active", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_return_receipt_tenant_id_received_unit_id_business_time",
                schema: "serialisation",
                table: "return_receipt",
                columns: new[] { "tenant_id", "received_unit_id", "business_time" });

            migrationBuilder.CreateIndex(
                name: "ix_return_receipt_tenant_id_rma_id_business_time",
                schema: "serialisation",
                table: "return_receipt",
                columns: new[] { "tenant_id", "rma_id", "business_time" });

            migrationBuilder.CreateIndex(
                name: "ix_return_receipt_tenant_id_rma_line_id",
                schema: "serialisation",
                table: "return_receipt",
                columns: new[] { "tenant_id", "rma_line_id" });

            migrationBuilder.CreateIndex(
                name: "ux_return_receipt_tenant_id_command_id_rma_line_id",
                schema: "serialisation",
                table: "return_receipt",
                columns: new[] { "tenant_id", "command_id", "rma_line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_returning_party_ref_stage",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "returning_party_ref", "stage" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_stage_requested_at",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "stage", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_rma_tenant_id_command_id",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rma_tenant_id_rma_number",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "rma_number" },
                unique: true,
                filter: "[rma_number] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rma_line_tenant_id_authorised_unit_id_receipt_outcome",
                schema: "serialisation",
                table: "rma_line",
                columns: new[] { "tenant_id", "authorised_unit_id", "receipt_outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_line_tenant_id_parent_unit_id_parent_incomplete",
                schema: "serialisation",
                table: "rma_line",
                columns: new[] { "tenant_id", "parent_unit_id", "parent_incomplete" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_line_tenant_id_requested_unit_id_ownership_episode_id",
                schema: "serialisation",
                table: "rma_line",
                columns: new[] { "tenant_id", "requested_unit_id", "ownership_episode_id" });

            migrationBuilder.CreateIndex(
                name: "ux_rma_line_tenant_id_rma_id_sequence",
                schema: "serialisation",
                table: "rma_line",
                columns: new[] { "tenant_id", "rma_id", "sequence" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "rma");
            migrationBuilder.AddTenantPolicy("serialisation", "rma_line");
            migrationBuilder.AddTenantPolicy("serialisation", "return_receipt");
            migrationBuilder.AddTenantPolicy("serialisation", "return_reason");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "return_reason");
            migrationBuilder.RemoveTenantPolicy("serialisation", "return_receipt");
            migrationBuilder.RemoveTenantPolicy("serialisation", "rma_line");
            migrationBuilder.RemoveTenantPolicy("serialisation", "rma");

            migrationBuilder.DropTable(
                name: "return_reason",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "return_receipt",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "rma_line",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "rma",
                schema: "serialisation");
        }
    }
}
