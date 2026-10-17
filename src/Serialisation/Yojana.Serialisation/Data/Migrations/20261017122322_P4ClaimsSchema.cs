using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4ClaimsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "claim",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_date = table.Column<DateOnly>(type: "date", nullable: false),
                    claimant_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    settlement_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    obligor_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_country = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    coverage_source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    remedy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    recovery_raised = table.Column<bool>(type: "bit", nullable: false),
                    entitlement_ids = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    denial_reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    chain_length = table.Column<int>(type: "int", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    adjudicated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    settled_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    submitted_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    last_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim", x => new { x.tenant_id, x.claim_id });
                    table.CheckConstraint("ck_claim_coverage_source", "coverage_source IN ('warranty', 'recall', 'goodwill', 'contract', 'chargeable')");
                    table.CheckConstraint("ck_claim_entitlement_ids_json", "ISJSON(entitlement_ids) = 1");
                    table.CheckConstraint("ck_claim_status", "status IN ('submitted', 'adjudicating', 'approved', 'partially_approved', 'denied', 'settled', 'closed')");
                });

            migrationBuilder.CreateTable(
                name: "claim_evidence",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_evidence", x => new { x.tenant_id, x.evidence_id });
                    table.ForeignKey(
                        name: "fk_claim_evidence_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "serialisation",
                        principalTable: "claim",
                        principalColumns: new[] { "tenant_id", "claim_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "claim_line",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    coverage_family = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    requested_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    coverage_pct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    approved_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    measurements = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    policy_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    accrual_cohort_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    coverage_source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    settlement_formula = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    denial_reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    chain_length = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_line", x => new { x.tenant_id, x.claim_line_id });
                    table.CheckConstraint("ck_claim_line_amounts", "requested_amount >= 0 AND approved_amount >= 0 AND approved_amount <= requested_amount");
                    table.CheckConstraint("ck_claim_line_coverage_pct", "coverage_pct >= 0 AND coverage_pct <= 100");
                    table.CheckConstraint("ck_claim_line_coverage_source", "coverage_source IN ('warranty', 'recall', 'goodwill', 'contract', 'chargeable')");
                    table.CheckConstraint("ck_claim_line_measurements_json", "ISJSON(measurements) = 1");
                    table.ForeignKey(
                        name: "fk_claim_line_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "serialisation",
                        principalTable: "claim",
                        principalColumns: new[] { "tenant_id", "claim_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recovery_claim",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    recovery_claim_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    customer_claim_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    inbound_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    child_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    vendor_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requested_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    approved_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    raised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    approved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    approved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    raised_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    approved_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_claim", x => new { x.tenant_id, x.recovery_claim_id });
                    table.CheckConstraint("ck_recovery_claim_amounts", "requested_amount >= 0 AND (approved_amount IS NULL OR (approved_amount >= 0 AND approved_amount <= requested_amount))");
                    table.CheckConstraint("ck_recovery_claim_status", "status IN ('raised', 'approved')");
                    table.ForeignKey(
                        name: "fk_recovery_claim_tenant_id_customer_claim_id",
                        columns: x => new { x.tenant_id, x.customer_claim_id },
                        principalSchema: "serialisation",
                        principalTable: "claim",
                        principalColumns: new[] { "tenant_id", "claim_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_status_submitted_at",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_unit_id_status_claim_date",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "unit_id", "status", "claim_date" });

            migrationBuilder.CreateIndex(
                name: "ux_claim_tenant_id_command_id",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_evidence_tenant_id_claim_id_recorded_at",
                schema: "serialisation",
                table: "claim_evidence",
                columns: new[] { "tenant_id", "claim_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_line_tenant_id_unit_id_entitlement_id",
                schema: "serialisation",
                table: "claim_line",
                columns: new[] { "tenant_id", "unit_id", "entitlement_id" });

            migrationBuilder.CreateIndex(
                name: "ux_claim_line_tenant_id_claim_id_sequence",
                schema: "serialisation",
                table: "claim_line",
                columns: new[] { "tenant_id", "claim_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recovery_claim_tenant_id_status_raised_at",
                schema: "serialisation",
                table: "recovery_claim",
                columns: new[] { "tenant_id", "status", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_claim_tenant_id_vendor_party_ref_status",
                schema: "serialisation",
                table: "recovery_claim",
                columns: new[] { "tenant_id", "vendor_party_ref", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_recovery_claim_tenant_id_customer_claim_id_claim_line_id_inbound_entitlement_id_child_unit_id",
                schema: "serialisation",
                table: "recovery_claim",
                columns: new[] { "tenant_id", "customer_claim_id", "claim_line_id", "inbound_entitlement_id", "child_unit_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "claim");
            migrationBuilder.AddTenantPolicy("serialisation", "claim_line");
            migrationBuilder.AddTenantPolicy("serialisation", "claim_evidence");
            migrationBuilder.AddTenantPolicy("serialisation", "recovery_claim");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "recovery_claim");
            migrationBuilder.RemoveTenantPolicy("serialisation", "claim_evidence");
            migrationBuilder.RemoveTenantPolicy("serialisation", "claim_line");
            migrationBuilder.RemoveTenantPolicy("serialisation", "claim");

            migrationBuilder.DropTable(
                name: "claim_evidence",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "claim_line",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "recovery_claim",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "claim",
                schema: "serialisation");
        }
    }
}
