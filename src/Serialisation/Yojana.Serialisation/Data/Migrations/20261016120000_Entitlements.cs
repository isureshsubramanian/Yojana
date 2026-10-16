using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Entitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accrual_cohort",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cohort_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    obligor_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period = table.Column<string>(type: "char(7)", unicode: false, fixedLength: true, maxLength: 7, nullable: false),
                    assurance_or_service = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accrual_cohort", x => new { x.tenant_id, x.cohort_id });
                });

            migrationBuilder.CreateTable(
                name: "coverage_family",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    family_code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    stacking = table.Column<bool>(type: "bit", nullable: false),
                    accounting_tag = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coverage_family", x => new { x.tenant_id, x.family_code });
                });

            migrationBuilder.CreateTable(
                name: "entitlement",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    scope_child_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    scope_item_class = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    coverage_family = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    owner_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    owner_party_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    obligor_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    payer_party_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    policy_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    start_trigger = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    start_basis = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    start_evidence_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    start_time_zone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    computed_by_rule_version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    duration_basis = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    coverage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    region_scope = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    transferable = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    condition_at_creation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    parent_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    root_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    replacement_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    accrual_cohort_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    revenue_schedule_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    assurance_or_service = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    closed_reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    closed_effective = table.Column<DateOnly>(type: "date", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    successor_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    successor_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    supersedes_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    confidence = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    statutory_extension_applied = table.Column<bool>(type: "bit", nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    source_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    source_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_document_line = table.Column<int>(type: "int", nullable: true),
                    customer_po_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    created_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    sys_end = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    sys_start = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement", x => new { x.tenant_id, x.entitlement_id });
                    table.CheckConstraint("ck_entitlement_closed_reason", "(status = 'closed' AND closed_reason IS NOT NULL) OR (status <> 'closed' AND closed_reason IS NULL)");
                    table.CheckConstraint("ck_entitlement_coverage_json", "ISJSON(coverage) = 1");
                    table.CheckConstraint("ck_entitlement_region_scope_json", "ISJSON(region_scope) = 1");
                    table.CheckConstraint("ck_entitlement_term", "end_date IS NULL OR (start_date IS NOT NULL AND end_date >= start_date)");
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "entitlement_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.CreateTable(
                name: "entitlement_amendment",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    amendment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    field = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    before_value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    after_value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    reason_code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    source_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    source_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    days_changed = table.Column<int>(type: "int", nullable: true),
                    approved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    customer_notification_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_amendment", x => new { x.tenant_id, x.amendment_id });
                });

            migrationBuilder.CreateTable(
                name: "entitlement_rebase",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rebase_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    old_start = table.Column<DateOnly>(type: "date", nullable: true),
                    new_start = table.Column<DateOnly>(type: "date", nullable: false),
                    old_end = table.Column<DateOnly>(type: "date", nullable: true),
                    new_end = table.Column<DateOnly>(type: "date", nullable: false),
                    evidence_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    policy_cap_applied = table.Column<bool>(type: "bit", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_rebase", x => new { x.tenant_id, x.rebase_id });
                });

            migrationBuilder.CreateTable(
                name: "entitlement_suspension",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    suspension_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    clock_paused = table.Column<bool>(type: "bit", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    resumed_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_suspension", x => new { x.tenant_id, x.suspension_id });
                    table.CheckConstraint("ck_entitlement_suspension_dates", "to_date IS NULL OR to_date >= from_date");
                });

            migrationBuilder.CreateTable(
                name: "entitlement_usage_limit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    meter_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    limit_value = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    used_value = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    period = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    resets_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_usage_limit", x => new { x.tenant_id, x.entitlement_id, x.meter_type });
                    table.CheckConstraint("ck_entitlement_usage_limit_values", "limit_value >= 0 AND used_value >= 0");
                });

            migrationBuilder.CreateTable(
                name: "replacement_policy",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_policy_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    rule = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    min_days = table.Column<int>(type: "int", nullable: true),
                    extension_months = table.Column<int>(type: "int", nullable: true),
                    description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_policy", x => new { x.tenant_id, x.replacement_policy_id });
                });

            migrationBuilder.CreateTable(
                name: "warranty_policy_version",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    policy_code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    coverage_family = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    entitlement_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    applies_to_contracts_from = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    rule_version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    duration_value = table.Column<int>(type: "int", nullable: false),
                    duration_unit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    end_of_month_rule = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    end_inclusive = table.Column<bool>(type: "bit", nullable: false),
                    duration_basis = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    usage_limits = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    start_trigger = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    fallback_chain = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    start_cap_days = table.Column<int>(type: "int", nullable: true),
                    coverage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    exclusions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    region_scope = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    transferable = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    settlement_formula = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    replacement_policy_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    refurbished_policies = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    doa_window_days = table.Column<int>(type: "int", nullable: true),
                    assurance_or_service = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    reference_time_zone_rule = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    service_contract_regulated = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    published_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    published_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    sys_end = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    sys_start = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warranty_policy_version", x => new { x.tenant_id, x.policy_version_id });
                    table.CheckConstraint("ck_warranty_policy_version_coverage_json", "ISJSON(coverage) = 1");
                    table.CheckConstraint("ck_warranty_policy_version_exclusions_json", "ISJSON(exclusions) = 1");
                    table.CheckConstraint("ck_warranty_policy_version_fallback_chain_json", "ISJSON(fallback_chain) = 1");
                    table.CheckConstraint("ck_warranty_policy_version_refurbished_policies_json", "ISJSON(refurbished_policies) = 1");
                    table.CheckConstraint("ck_warranty_policy_version_region_scope_json", "ISJSON(region_scope) = 1");
                    table.CheckConstraint("ck_warranty_policy_version_usage_limits_json", "ISJSON(usage_limits) = 1");
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "warranty_policy_version_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.CreateIndex(
                name: "ux_accrual_cohort_tenant_id_obligor_entity_id_item_id_period_assurance_or_service",
                schema: "serialisation",
                table: "accrual_cohort",
                columns: new[] { "tenant_id", "obligor_entity_id", "item_id", "period", "assurance_or_service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_owner_party_ref_status",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "owner_party_ref", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_policy_version_id",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "policy_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_root_entitlement_id",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "root_entitlement_id" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_source_document_type_source_document_id",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "source_document_type", "source_document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_unit_id_created_at",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "unit_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_entitlement_tenant_id_unit_id_coverage_family",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "unit_id", "coverage_family" },
                unique: true,
                filter: "[status] <> 'closed'");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_amendment_tenant_id_entitlement_id_at",
                schema: "serialisation",
                table: "entitlement_amendment",
                columns: new[] { "tenant_id", "entitlement_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_rebase_tenant_id_entitlement_id_at",
                schema: "serialisation",
                table: "entitlement_rebase",
                columns: new[] { "tenant_id", "entitlement_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_suspension_tenant_id_entitlement_id_from_date",
                schema: "serialisation",
                table: "entitlement_suspension",
                columns: new[] { "tenant_id", "entitlement_id", "from_date" });

            migrationBuilder.CreateIndex(
                name: "ux_entitlement_suspension_tenant_id_entitlement_id",
                schema: "serialisation",
                table: "entitlement_suspension",
                columns: new[] { "tenant_id", "entitlement_id" },
                unique: true,
                filter: "[to_date] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_replacement_policy_tenant_id_code",
                schema: "serialisation",
                table: "replacement_policy",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_warranty_policy_version_tenant_id_coverage_family_status",
                schema: "serialisation",
                table: "warranty_policy_version",
                columns: new[] { "tenant_id", "coverage_family", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_policy_version_tenant_id_policy_code_applies_to_contracts_from",
                schema: "serialisation",
                table: "warranty_policy_version",
                columns: new[] { "tenant_id", "policy_code", "applies_to_contracts_from" });

            migrationBuilder.CreateIndex(
                name: "ux_warranty_policy_version_tenant_id_policy_code_version",
                schema: "serialisation",
                table: "warranty_policy_version",
                columns: new[] { "tenant_id", "policy_code", "version" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "coverage_family");
            migrationBuilder.AddTenantPolicy("serialisation", "replacement_policy");
            migrationBuilder.AddTenantPolicy("serialisation", "warranty_policy_version");
            migrationBuilder.AddTenantPolicy("serialisation", "accrual_cohort");
            migrationBuilder.AddTenantPolicy("serialisation", "entitlement");
            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_suspension");
            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_rebase");
            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_amendment");
            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_usage_limit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_usage_limit");
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_amendment");
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_rebase");
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_suspension");
            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement");
            migrationBuilder.RemoveTenantPolicy("serialisation", "accrual_cohort");
            migrationBuilder.RemoveTenantPolicy("serialisation", "warranty_policy_version");
            migrationBuilder.RemoveTenantPolicy("serialisation", "replacement_policy");
            migrationBuilder.RemoveTenantPolicy("serialisation", "coverage_family");

            migrationBuilder.DropTable(
                name: "accrual_cohort",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "coverage_family",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "entitlement",
                schema: "serialisation")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "entitlement_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.DropTable(
                name: "entitlement_amendment",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "entitlement_rebase",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "entitlement_suspension",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "entitlement_usage_limit",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "replacement_policy",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "warranty_policy_version",
                schema: "serialisation")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "warranty_policy_version_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");
        }
    }
}
