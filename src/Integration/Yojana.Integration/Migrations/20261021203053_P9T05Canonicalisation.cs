using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T05Canonicalisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "item_name",
                schema: "migration",
                table: "stg_src_oitm",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql("""
                CREATE OR ALTER FUNCTION [migration].[canonical_serial]
                (
                    @serial nvarchar(500),
                    @strip_hyphens bit,
                    @strip_spaces bit,
                    @strip_leading_zeros bit
                )
                RETURNS nvarchar(500)
                AS
                BEGIN
                    IF @serial IS NULL RETURN NULL;
                    DECLARE @value nvarchar(500) = UPPER(TRIM(@serial));
                    IF @strip_hyphens = 1 SET @value = REPLACE(@value, N'-', N'');
                    IF @strip_spaces = 1 SET @value = REPLACE(@value, N' ', N'');
                    IF @strip_leading_zeros = 1
                    BEGIN
                        DECLARE @first_nonzero int = PATINDEX(N'%[^0]%', @value);
                        SET @value = CASE WHEN @first_nonzero = 0 THEN N''
                                          ELSE SUBSTRING(@value, @first_nonzero, 500) END;
                    END;
                    RETURN @value;
                END
                """);

            migrationBuilder.CreateTable(
                name: "migration_canon_run",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    profile_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    failure_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    source_serial_count = table.Column<long>(type: "bigint", nullable: false),
                    exact_count = table.Column<long>(type: "bigint", nullable: false),
                    trimmed_upper_count = table.Column<long>(type: "bigint", nullable: false),
                    family_normalised_count = table.Column<long>(type: "bigint", nullable: false),
                    waiting_count = table.Column<long>(type: "bigint", nullable: false),
                    merged_count = table.Column<long>(type: "bigint", nullable: false),
                    kept_apart_count = table.Column<long>(type: "bigint", nullable: false),
                    fix_in_incumbent_count = table.Column<long>(type: "bigint", nullable: false),
                    carried_over_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_canon_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_migration_canon_run_status", "status IN ('queued','running','completed','failed')");
                    table.ForeignKey(
                        name: "fk_migration_canon_run_tenant_id_batch_id",
                        columns: x => new { x.tenant_id, x.batch_id },
                        principalSchema: "migration",
                        principalTable: "migration_batch",
                        principalColumns: new[] { "tenant_id", "batch_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_migration_canon_run_tenant_id_profile_run_id",
                        columns: x => new { x.tenant_id, x.profile_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_profile_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_review_item",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    review_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    correlation_key = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    canonical_key = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    item_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    family = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    stored_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    rule_tried = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_keys_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    candidate_source_keys_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    candidate_summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    suggestion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    suggested_keep_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    suggestion_reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    hint = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    applied_decision_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_review_item", x => new { x.tenant_id, x.review_item_id });
                    table.CheckConstraint("ck_migration_review_item_kind", "kind IN ('same_serial_one_item','same_serial_different_items','orphan_equipment_card','serial_in_user_field')");
                    table.CheckConstraint("ck_migration_review_item_status", "status IN ('waiting','decided','carried_over')");
                    table.ForeignKey(
                        name: "fk_migration_review_item_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_canon_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_unit_candidate",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    primary_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    item_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    family = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    canonical_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    stored_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    trimmed_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    family_normalised_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    match_rule = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    disposition = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_unit_candidate", x => new { x.tenant_id, x.candidate_id });
                    table.CheckConstraint("ck_migration_unit_candidate_disposition", "disposition IN ('active','review','merged','excluded')");
                    table.CheckConstraint("ck_migration_unit_candidate_provenance", "provenance IN ('source','unverified')");
                    table.CheckConstraint("ck_migration_unit_candidate_rule", "match_rule IN ('exact','trimmed_upper','family_normalised','reviewed')");
                    table.ForeignKey(
                        name: "fk_migration_unit_candidate_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_canon_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_review_decision",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    decision_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    review_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    correlation_key = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    decision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    keep_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    decided_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_review_decision", x => new { x.tenant_id, x.decision_id });
                    table.CheckConstraint("ck_migration_review_decision_kind", "decision IN ('merge_into','keep_apart','fix_in_incumbent')");
                    table.ForeignKey(
                        name: "fk_migration_review_decision_tenant_id_review_item_id",
                        columns: x => new { x.tenant_id, x.review_item_id },
                        principalSchema: "migration",
                        principalTable: "migration_review_item",
                        principalColumns: new[] { "tenant_id", "review_item_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_entitlement_candidate",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    serial_master_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    start_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    end_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    match_rule = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_entitlement_candidate", x => new { x.tenant_id, x.entitlement_candidate_id });
                    table.CheckConstraint("ck_migration_entitlement_candidate_provenance", "provenance IN ('source','unverified')");
                    table.CheckConstraint("ck_migration_entitlement_candidate_rule", "match_rule IN ('exact','trimmed_upper','family_normalised','reviewed')");
                    table.CheckConstraint("ck_migration_entitlement_candidate_source_kind", "source_kind IN ('equipment_card','contract','contract_line')");
                    table.ForeignKey(
                        name: "fk_migration_entitlement_candidate_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_event_candidate",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    serial_master_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    transaction_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    match_rule = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_event_candidate", x => new { x.tenant_id, x.event_candidate_id });
                    table.CheckConstraint("ck_migration_event_candidate_provenance", "provenance IN ('source','unverified')");
                    table.CheckConstraint("ck_migration_event_candidate_rule", "match_rule IN ('exact','trimmed_upper','family_normalised','reviewed')");
                    table.ForeignKey(
                        name: "fk_migration_event_candidate_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_unit_candidate_source",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_source_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    matched_serial_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_unit_candidate_source", x => new { x.tenant_id, x.candidate_source_id });
                    table.CheckConstraint("ck_migration_unit_candidate_source_kind", "source_kind IN ('serial_master','equipment_card','contract','contract_line')");
                    table.CheckConstraint("ck_migration_unit_candidate_source_role", "role IN ('kept','merged')");
                    table.ForeignKey(
                        name: "fk_migration_unit_candidate_source_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_canon_run_tenant_id_batch_id_requested_at_run_id",
                schema: "migration",
                table: "migration_canon_run",
                columns: new[] { "tenant_id", "batch_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_canon_run_tenant_id_profile_run_id_run_id",
                schema: "migration",
                table: "migration_canon_run",
                columns: new[] { "tenant_id", "profile_run_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_canon_run_tenant_id_status_requested_at_run_id",
                schema: "migration",
                table: "migration_canon_run",
                columns: new[] { "tenant_id", "status", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_entitlement_candidate_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_entitlement_candidate",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_entitlement_candidate_tenant_id_run_id_candidate_id",
                schema: "migration",
                table: "migration_entitlement_candidate",
                columns: new[] { "tenant_id", "run_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_entitlement_candidate_tenant_id_run_id_candidate_id_source_kind_source_key",
                schema: "migration",
                table: "migration_entitlement_candidate",
                columns: new[] { "tenant_id", "run_id", "candidate_id", "source_kind", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_event_candidate_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_event_candidate",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_event_candidate_tenant_id_run_id_candidate_id",
                schema: "migration",
                table: "migration_event_candidate",
                columns: new[] { "tenant_id", "run_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_event_candidate_tenant_id_run_id_transaction_source_key",
                schema: "migration",
                table: "migration_event_candidate",
                columns: new[] { "tenant_id", "run_id", "transaction_source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_decision_tenant_id_correlation_key_decided_at_decision_id",
                schema: "migration",
                table: "migration_review_decision",
                columns: new[] { "tenant_id", "correlation_key", "decided_at", "decision_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_decision_tenant_id_fingerprint_decided_at_decision_id",
                schema: "migration",
                table: "migration_review_decision",
                columns: new[] { "tenant_id", "fingerprint", "decided_at", "decision_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_decision_tenant_id_review_item_id_decided_at_decision_id",
                schema: "migration",
                table: "migration_review_decision",
                columns: new[] { "tenant_id", "review_item_id", "decided_at", "decision_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_item_tenant_id_batch_id_status_created_at_review_item_id",
                schema: "migration",
                table: "migration_review_item",
                columns: new[] { "tenant_id", "batch_id", "status", "created_at", "review_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_item_tenant_id_correlation_key_created_at_review_item_id",
                schema: "migration",
                table: "migration_review_item",
                columns: new[] { "tenant_id", "correlation_key", "created_at", "review_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_item_tenant_id_fingerprint_created_at_review_item_id",
                schema: "migration",
                table: "migration_review_item",
                columns: new[] { "tenant_id", "fingerprint", "created_at", "review_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_review_item_tenant_id_run_id_kind_family_status_created_at_review_item_id",
                schema: "migration",
                table: "migration_review_item",
                columns: new[] { "tenant_id", "run_id", "kind", "family", "status", "created_at", "review_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_family_family_normalised_serial",
                schema: "migration",
                table: "migration_unit_candidate",
                columns: new[] { "tenant_id", "run_id", "family", "family_normalised_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_tenant_id_run_id_item_code_canonical_serial",
                schema: "migration",
                table: "migration_unit_candidate",
                columns: new[] { "tenant_id", "run_id", "item_code", "canonical_serial" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_unit_candidate_tenant_id_run_id_primary_source_key",
                schema: "migration",
                table: "migration_unit_candidate",
                columns: new[] { "tenant_id", "run_id", "primary_source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_source_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_unit_candidate_source",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_source_tenant_id_run_id_candidate_id",
                schema: "migration",
                table: "migration_unit_candidate_source",
                columns: new[] { "tenant_id", "run_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_unit_candidate_source_tenant_id_run_id_candidate_id_source_kind_source_key",
                schema: "migration",
                table: "migration_unit_candidate_source",
                columns: new[] { "tenant_id", "run_id", "candidate_id", "source_kind", "source_key" },
                unique: true);

            migrationBuilder.AddTenantPolicy("migration", "migration_canon_run");
            migrationBuilder.AddTenantPolicy("migration", "migration_unit_candidate");
            migrationBuilder.AddTenantPolicy("migration", "migration_unit_candidate_source");
            migrationBuilder.AddTenantPolicy("migration", "migration_event_candidate");
            migrationBuilder.AddTenantPolicy("migration", "migration_entitlement_candidate");
            migrationBuilder.AddTenantPolicy("migration", "migration_review_item");
            migrationBuilder.AddTenantPolicy("migration", "migration_review_decision");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "migration_review_decision");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_review_item");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_entitlement_candidate");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_event_candidate");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_unit_candidate_source");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_unit_candidate");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_canon_run");

            migrationBuilder.DropTable(
                name: "migration_entitlement_candidate",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_event_candidate",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_review_decision",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_unit_candidate_source",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_review_item",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_unit_candidate",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_canon_run",
                schema: "migration");

            migrationBuilder.DropColumn(
                name: "item_name",
                schema: "migration",
                table: "stg_src_oitm");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS [migration].[canonical_serial];");
        }
    }
}
