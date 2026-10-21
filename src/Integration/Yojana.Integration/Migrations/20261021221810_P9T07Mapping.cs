using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T07Mapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_map_run",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconstruction_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cutover_date = table.Column<DateOnly>(type: "date", nullable: false),
                    allowed_waiting_reviews = table.Column<bool>(type: "bit", nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    failure_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    unit_count = table.Column<long>(type: "bigint", nullable: false),
                    event_count = table.Column<long>(type: "bigint", nullable: false),
                    excluded_candidate_count = table.Column<long>(type: "bigint", nullable: false),
                    waiting_candidate_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_map_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_migration_map_run_status", "status IN ('queued','running','completed','failed')");
                    table.ForeignKey(
                        name: "fk_migration_map_run_tenant_id_reconstruction_run_id",
                        columns: x => new { x.tenant_id, x.reconstruction_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_reconstruction_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_unit",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    item_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    origin = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    history_depth = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    location_state = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    warehouse_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    custody_location_kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    condition_grade = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    condition_provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_unit", x => new { x.tenant_id, x.plan_unit_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_unit_tenant_id_map_run_id",
                        columns: x => new { x.tenant_id, x.map_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_map_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_attachment",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_attachment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    path = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    file_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    content_hash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_attachment", x => new { x.tenant_id, x.plan_attachment_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_attachment_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_certificate",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_certificate", x => new { x.tenant_id, x.plan_certificate_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_certificate_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_entitlement",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    coverage_family = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    policy_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    policy_version = table.Column<int>(type: "int", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    termination_date = table.Column<DateOnly>(type: "date", nullable: true),
                    remaining_days = table.Column<int>(type: "int", nullable: false),
                    expired = table.Column<bool>(type: "bit", nullable: false),
                    direction = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    date_source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_entitlement", x => new { x.tenant_id, x.plan_entitlement_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_entitlement_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_event",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    history_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    movement_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    effective_date = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    document_type_label = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    document_number = table.Column<int>(type: "int", nullable: true),
                    party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    warehouse_code = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_event", x => new { x.tenant_id, x.plan_event_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_event_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_gap",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_gap_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_gap", x => new { x.tenant_id, x.plan_gap_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_gap_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_identifier",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_identifier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    identifier_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    raw_value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    issuer_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_data_value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    provenance = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_identifier", x => new { x.tenant_id, x.plan_identifier_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_identifier_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_log",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_log_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    related_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_log", x => new { x.tenant_id, x.plan_log_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_log_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_ownership",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_ownership_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    party_name = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    party_reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    pseudonymised = table.Column<bool>(type: "bit", nullable: false),
                    is_current = table.Column<bool>(type: "bit", nullable: false),
                    evidence_source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    evidence_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_ownership", x => new { x.tenant_id, x.plan_ownership_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_ownership_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_replacement_link",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_replacement_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replaced_plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    policy_applied = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_replacement_link", x => new { x.tenant_id, x.plan_replacement_link_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_replacement_link_tenant_id_replaced_plan_unit_id",
                        columns: x => new { x.tenant_id, x.replaced_plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_plan_replacement_link_tenant_id_replacement_plan_unit_id",
                        columns: x => new { x.tenant_id, x.replacement_plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_plan_warranty_evidence",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_warranty_evidence_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    termination_date = table.Column<DateOnly>(type: "date", nullable: true),
                    direction = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    selected_for_entitlement = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_plan_warranty_evidence", x => new { x.tenant_id, x.plan_warranty_evidence_id });
                    table.ForeignKey(
                        name: "fk_migration_plan_warranty_evidence_tenant_id_plan_unit_id",
                        columns: x => new { x.tenant_id, x.plan_unit_id },
                        principalSchema: "migration",
                        principalTable: "migration_plan_unit",
                        principalColumns: new[] { "tenant_id", "plan_unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_map_run_tenant_id_batch_id_requested_at_run_id",
                schema: "migration",
                table: "migration_map_run",
                columns: new[] { "tenant_id", "batch_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_map_run_tenant_id_reconstruction_run_id_requested_at_run_id",
                schema: "migration",
                table: "migration_map_run",
                columns: new[] { "tenant_id", "reconstruction_run_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_map_run_tenant_id_status_requested_at_run_id",
                schema: "migration",
                table: "migration_map_run",
                columns: new[] { "tenant_id", "status", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_attachment_tenant_id_map_run_id_plan_unit_id_plan_attachment_id",
                schema: "migration",
                table: "migration_plan_attachment",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "plan_attachment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_attachment_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_attachment",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_certificate_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_certificate",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_plan_certificate_tenant_id_map_run_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_certificate",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_entitlement_tenant_id_map_run_id_plan_unit_id_coverage_family_plan_entitlement_id",
                schema: "migration",
                table: "migration_plan_entitlement",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "coverage_family", "plan_entitlement_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_entitlement_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_entitlement",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_event_tenant_id_map_run_id_provenance_plan_event_id",
                schema: "migration",
                table: "migration_plan_event",
                columns: new[] { "tenant_id", "map_run_id", "provenance", "plan_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_event_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_event",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_plan_event_tenant_id_map_run_id_plan_unit_id_sequence",
                schema: "migration",
                table: "migration_plan_event",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_gap_tenant_id_map_run_id_kind_plan_gap_id",
                schema: "migration",
                table: "migration_plan_gap",
                columns: new[] { "tenant_id", "map_run_id", "kind", "plan_gap_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_gap_tenant_id_map_run_id_plan_unit_id_plan_gap_id",
                schema: "migration",
                table: "migration_plan_gap",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "plan_gap_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_gap_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_gap",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_identifier_tenant_id_map_run_id_plan_unit_id_plan_identifier_id",
                schema: "migration",
                table: "migration_plan_identifier",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "plan_identifier_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_identifier_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_identifier",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_log_tenant_id_map_run_id_plan_unit_id_plan_log_id",
                schema: "migration",
                table: "migration_plan_log",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "plan_log_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_log_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_log",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_ownership_tenant_id_map_run_id_plan_unit_id_plan_ownership_id",
                schema: "migration",
                table: "migration_plan_ownership",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "plan_ownership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_ownership_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_ownership",
                columns: new[] { "tenant_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_replacement_link_tenant_id_replaced_plan_unit_id",
                schema: "migration",
                table: "migration_plan_replacement_link",
                columns: new[] { "tenant_id", "replaced_plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_replacement_link_tenant_id_replacement_plan_unit_id",
                schema: "migration",
                table: "migration_plan_replacement_link",
                columns: new[] { "tenant_id", "replacement_plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_plan_replacement_link_tenant_id_map_run_id_replaced_plan_unit_id_replacement_plan_unit_id",
                schema: "migration",
                table: "migration_plan_replacement_link",
                columns: new[] { "tenant_id", "map_run_id", "replaced_plan_unit_id", "replacement_plan_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_unit_tenant_id_map_run_id_item_code_plan_unit_id",
                schema: "migration",
                table: "migration_plan_unit",
                columns: new[] { "tenant_id", "map_run_id", "item_code", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_plan_unit_tenant_id_map_run_id_candidate_id",
                schema: "migration",
                table: "migration_plan_unit",
                columns: new[] { "tenant_id", "map_run_id", "candidate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_warranty_evidence_tenant_id_map_run_id_plan_unit_id_source_kind_plan_warranty_evidence_id",
                schema: "migration",
                table: "migration_plan_warranty_evidence",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id", "source_kind", "plan_warranty_evidence_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_warranty_evidence_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_warranty_evidence",
                columns: new[] { "tenant_id", "plan_unit_id" });

            foreach (var table in new[]
            {
                "migration_map_run", "migration_plan_unit", "migration_plan_identifier",
                "migration_plan_event", "migration_plan_ownership", "migration_plan_replacement_link",
                "migration_plan_entitlement", "migration_plan_warranty_evidence", "migration_plan_attachment",
                "migration_plan_certificate", "migration_plan_gap", "migration_plan_log",
            })
                migrationBuilder.AddTenantPolicy("migration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
            {
                "migration_plan_log", "migration_plan_gap", "migration_plan_certificate",
                "migration_plan_attachment", "migration_plan_warranty_evidence", "migration_plan_entitlement",
                "migration_plan_replacement_link", "migration_plan_ownership", "migration_plan_event",
                "migration_plan_identifier", "migration_plan_unit", "migration_map_run",
            })
                migrationBuilder.RemoveTenantPolicy("migration", table);

            migrationBuilder.DropTable(
                name: "migration_plan_attachment",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_certificate",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_entitlement",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_event",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_gap",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_identifier",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_log",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_ownership",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_replacement_link",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_warranty_evidence",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_plan_unit",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_map_run",
                schema: "migration");
        }
    }
}
