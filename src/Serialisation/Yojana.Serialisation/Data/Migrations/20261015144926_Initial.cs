using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "serialisation");

            // Per-tenant monotonic event sequence (ADR-0001 DDL: CACHE 1000; EF Core has no cache option).
            migrationBuilder.CreateSequence(
                name: "seq_unit_event",
                schema: "serialisation");
            migrationBuilder.Sql("ALTER SEQUENCE [serialisation].[seq_unit_event] CACHE 1000;");

            // The ledger is range-partitioned monthly on event_time; EF Core cannot express partitioning, so the
            // function, scheme, both ledger tables and their aligned indexes are T-SQL (LedgerDdl).
            LedgerDdl.CreatePartitioning(migrationBuilder);
            LedgerDdl.CreateLedgerTable(migrationBuilder, "unit_event", "ux_unit_event_tenant_id_idempotency_key_event_time", "ix_unit_event_tenant_id_unit_id_event_time");
            LedgerDdl.CreateLedgerTable(migrationBuilder, "unit_event_archive", "ux_unit_event_archive_tenant_id_idempotency_key_event_time", "ix_unit_event_archive_tenant_id_unit_id_event_time");

            migrationBuilder.CreateTable(
                name: "assembly_link",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    child_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    child_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    child_lot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    relation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    slot = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    installed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    removed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    installing_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    removing_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    through_phantom_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    release_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    release_doc_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assembly_link", x => new { x.tenant_id, x.link_id });
                    table.CheckConstraint("ck_assembly_link_child", "(child_unit_id IS NOT NULL AND child_item_id IS NULL AND child_lot_id IS NULL AND quantity IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL AND quantity IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "build_record",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    build_record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    released_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    release_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    model_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    revision = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    facility_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    payload_hash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    release_signature_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_record", x => new { x.tenant_id, x.build_record_id });
                    table.CheckConstraint("ck_build_record_payload_json", "ISJSON(payload) = 1");
                });

            migrationBuilder.CreateTable(
                name: "custody_episode",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    party_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    location_kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    due_back = table.Column<DateOnly>(type: "date", nullable: true),
                    source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    confidence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    valid_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    valid_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    opening_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    opening_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    opening_document_line = table.Column<int>(type: "int", nullable: true),
                    opening_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    closing_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    closing_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    closing_document_line = table.Column<int>(type: "int", nullable: true),
                    closing_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    party_is_group_member = table.Column<bool>(type: "bit", nullable: false),
                    external_order_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    customer_po_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    customer_item_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    sys_end = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    sys_start = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custody_episode", x => new { x.tenant_id, x.episode_id });
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "custody_episode_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.CreateTable(
                name: "serial_range",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    range_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    scheme = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    enterprise_identifier = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    qualifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    start_value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    end_value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    start_ordinal = table.Column<long>(type: "bigint", nullable: true),
                    end_ordinal = table.Column<long>(type: "bigint", nullable: true),
                    count = table.Column<int>(type: "int", nullable: false),
                    randomised_list_ref = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    allocated_to_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    allocated_to_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    allocated_to_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    reserved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    reserved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    commissioned_count = table.Column<int>(type: "int", nullable: false),
                    note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_serial_range", x => new { x.tenant_id, x.range_id });
                    table.CheckConstraint("ck_serial_range_bounds", "(start_ordinal IS NULL AND end_ordinal IS NULL) OR (start_ordinal <= end_ordinal AND end_ordinal - start_ordinal + 1 = count)");
                });

            migrationBuilder.CreateTable(
                name: "unit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    lifecycle_status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    overlay_flags = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    origin = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    brand_is_ours = table.Column<bool>(type: "bit", nullable: false),
                    lot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    configuration_snapshot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    derived_from_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    merged_into_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    serialisation_point = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    retention_class = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    history_depth = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "full"),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sys_end = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    sys_start = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit", x => new { x.tenant_id, x.unit_id });
                    table.CheckConstraint("ck_unit_overlay_flags_json", "ISJSON(overlay_flags) = 1");
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "unit_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.CreateTable(
                name: "unit_current",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    model_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lifecycle_status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    disposition = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    condition_grade = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    location_kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    custody_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    custody_kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    owner_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    open_ownership_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    open_custody_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    live_entitlement_ids = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    warranty_end_max = table.Column<DateOnly>(type: "date", nullable: true),
                    overlay_flags = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    last_event_seq = table.Column<long>(type: "bigint", nullable: false),
                    last_event_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    attributes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_current", x => new { x.tenant_id, x.unit_id });
                    table.CheckConstraint("ck_unit_current_attributes_json", "attributes IS NULL OR ISJSON(attributes) = 1");
                    table.CheckConstraint("ck_unit_current_live_entitlement_ids_json", "ISJSON(live_entitlement_ids) = 1");
                    table.CheckConstraint("ck_unit_current_overlay_flags_json", "ISJSON(overlay_flags) = 1");
                });

            migrationBuilder.CreateTable(
                name: "unit_identifier",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    identifier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    scheme = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    issuer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    enterprise_identifier = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    qualifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    raw_value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    normalised_value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    scope_key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    valid_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    valid_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    superseded_by_identifier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    supersedes_identifier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    correction_reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    duplicate_acknowledged = table.Column<bool>(type: "bit", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_identifier", x => new { x.tenant_id, x.identifier_id });
                });

            migrationBuilder.CreateTable(
                name: "unit_item_assignment",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    model_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    valid_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    valid_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_item_assignment", x => new { x.tenant_id, x.assignment_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_assembly_link_tenant_id_parent_unit_id_removed_at",
                schema: "serialisation",
                table: "assembly_link",
                columns: new[] { "tenant_id", "parent_unit_id", "removed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_assembly_link_tenant_id_child_unit_id",
                schema: "serialisation",
                table: "assembly_link",
                columns: new[] { "tenant_id", "child_unit_id" },
                unique: true,
                filter: "[removed_at] IS NULL AND [child_unit_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_build_record_tenant_id_unit_id_version",
                schema: "serialisation",
                table: "build_record",
                columns: new[] { "tenant_id", "unit_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_location_id_valid_to",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "location_id", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_party_ref_kind_valid_to",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "party_ref", "kind", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_unit_id_valid_from",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "unit_id", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ux_custody_episode_tenant_id_unit_id_kind",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "unit_id", "kind" },
                unique: true,
                filter: "[valid_to] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_tenant_id_allocated_to_document_id",
                schema: "serialisation",
                table: "serial_range",
                columns: new[] { "tenant_id", "allocated_to_document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_tenant_id_scheme_qualifier_start_ordinal",
                schema: "serialisation",
                table: "serial_range",
                columns: new[] { "tenant_id", "scheme", "qualifier", "start_ordinal" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_tenant_id_derived_from_unit_id",
                schema: "serialisation",
                table: "unit",
                columns: new[] { "tenant_id", "derived_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_tenant_id_lifecycle_status",
                schema: "serialisation",
                table: "unit",
                columns: new[] { "tenant_id", "lifecycle_status" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_tenant_id_lot_id",
                schema: "serialisation",
                table: "unit",
                columns: new[] { "tenant_id", "lot_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_current_tenant_id_custody_party_ref_custody_kind",
                schema: "serialisation",
                table: "unit_current",
                columns: new[] { "tenant_id", "custody_party_ref", "custody_kind" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_current_tenant_id_item_id_location_id_disposition",
                schema: "serialisation",
                table: "unit_current",
                columns: new[] { "tenant_id", "item_id", "location_id", "disposition" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_current_tenant_id_owner_party_ref",
                schema: "serialisation",
                table: "unit_current",
                columns: new[] { "tenant_id", "owner_party_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_identifier_tenant_id_normalised_value",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "normalised_value" })
                .Annotation("SqlServer:Include", new[] { "unit_id", "type", "scope_key", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "ux_unit_identifier_tenant_id_scheme_scope_key_normalised_value",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "scheme", "scope_key", "normalised_value" },
                unique: true,
                filter: "[valid_to] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_unit_identifier_tenant_id_unit_id_type",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "unit_id", "type" },
                unique: true,
                filter: "[valid_to] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_unit_item_assignment_tenant_id_item_id_valid_to",
                schema: "serialisation",
                table: "unit_item_assignment",
                columns: new[] { "tenant_id", "item_id", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "ux_unit_item_assignment_tenant_id_unit_id",
                schema: "serialisation",
                table: "unit_item_assignment",
                columns: new[] { "tenant_id", "unit_id" },
                unique: true,
                filter: "[valid_to] IS NULL");

            // Row-level security on every tenant table of this schema (MOD-01 invariant 1): one policy per schema,
            // FILTER plus BLOCK predicates on the shared sec.fn_tenant_predicate. Both ledger tables carry the same
            // predicates so SWITCH PARTITION between them is permitted.
            migrationBuilder.AddTenantPolicy("serialisation", "unit");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_identifier");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_item_assignment");
            migrationBuilder.AddTenantPolicy("serialisation", "assembly_link");
            migrationBuilder.AddTenantPolicy("serialisation", "build_record");
            migrationBuilder.AddTenantPolicy("serialisation", "serial_range");
            migrationBuilder.AddTenantPolicy("serialisation", "custody_episode");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_event");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_event_archive");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_current");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_current");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_event_archive");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_event");
            migrationBuilder.RemoveTenantPolicy("serialisation", "custody_episode");
            migrationBuilder.RemoveTenantPolicy("serialisation", "serial_range");
            migrationBuilder.RemoveTenantPolicy("serialisation", "build_record");
            migrationBuilder.RemoveTenantPolicy("serialisation", "assembly_link");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_item_assignment");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_identifier");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'tenant_policy_serialisation') EXEC(N'DROP SECURITY POLICY [sec].[tenant_policy_serialisation]');");
            migrationBuilder.DropTable(
                name: "assembly_link",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "build_record",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "custody_episode",
                schema: "serialisation")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "custody_episode_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.DropTable(
                name: "serial_range",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit",
                schema: "serialisation")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "unit_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.DropTable(
                name: "unit_current",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_event",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_event_archive",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_identifier",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_item_assignment",
                schema: "serialisation");

            migrationBuilder.DropSequence(
                name: "seq_unit_event",
                schema: "serialisation");

            LedgerDdl.DropPartitioning(migrationBuilder);
        }
    }
}
