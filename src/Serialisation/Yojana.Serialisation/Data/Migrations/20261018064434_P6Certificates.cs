using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6Certificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_template",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    document_kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    form_type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    applies_to_item_class = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    built_in = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_template", x => new { x.tenant_id, x.template_id });
                    table.CheckConstraint("ck_certificate_template_document_kind", "document_kind IN ('release_record', 'warranty_certificate')");
                    table.CheckConstraint("ck_certificate_template_form_type", "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'custom')");
                });

            migrationBuilder.CreateTable(
                name: "certificate_template_version",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    language = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    payload_schema_version = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    effective_from = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    effective_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    layout_kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    layout_body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    page_size = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    orientation = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    margin_top_mm = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: false),
                    margin_right_mm = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: false),
                    margin_bottom_mm = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: false),
                    margin_left_mm = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: false),
                    header_body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    footer_body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    branding_asset_ref = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    static_text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    fields = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    approved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    approved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_template_version", x => new { x.tenant_id, x.template_version_id });
                    table.CheckConstraint("ck_certificate_template_version_dates", "effective_to IS NULL OR (effective_from IS NOT NULL AND effective_to > effective_from)");
                    table.CheckConstraint("ck_certificate_template_version_fields_json", "ISJSON(fields) = 1");
                    table.CheckConstraint("ck_certificate_template_version_layout", "layout_kind = 'html'");
                    table.CheckConstraint("ck_certificate_template_version_static_text_json", "ISJSON(static_text) = 1");
                    table.CheckConstraint("ck_certificate_template_version_status", "status IN ('draft', 'active', 'retired')");
                    table.ForeignKey(
                        name: "fk_certificate_template_version_tenant_id_template_id",
                        columns: x => new { x.tenant_id, x.template_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_template",
                        principalColumns: new[] { "tenant_id", "template_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "certificate_instance",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    form_type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    tracking_number = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    as_built_revision_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    facts_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    supersedes_certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    supersede_reason = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    triggering_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issuer_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issuer_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    owner_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    branding_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    language = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    provenance = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    unknowns = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    evidence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    issued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_instance", x => new { x.tenant_id, x.certificate_id });
                    table.CheckConstraint("ck_certificate_instance_branding_json", "ISJSON(branding_snapshot) = 1");
                    table.CheckConstraint("ck_certificate_instance_document_kind", "document_kind IN ('release_record', 'warranty_certificate')");
                    table.CheckConstraint("ck_certificate_instance_evidence_json", "ISJSON(evidence) = 1");
                    table.CheckConstraint("ck_certificate_instance_form_type", "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'custom')");
                    table.CheckConstraint("ck_certificate_instance_issuer_json", "ISJSON(issuer_snapshot) = 1");
                    table.CheckConstraint("ck_certificate_instance_owner_json", "owner_snapshot IS NULL OR ISJSON(owner_snapshot) = 1");
                    table.CheckConstraint("ck_certificate_instance_owner_kind", "(document_kind = 'release_record' AND owner_snapshot IS NULL) OR document_kind = 'warranty_certificate'");
                    table.CheckConstraint("ck_certificate_instance_payload_json", "ISJSON(payload) = 1");
                    table.CheckConstraint("ck_certificate_instance_provenance", "provenance IN ('system_generated', 'migrated', 'reconstructed')");
                    table.CheckConstraint("ck_certificate_instance_reconstruction", "provenance = 'system_generated' OR (JSON_QUERY(unknowns) <> '[]' AND JSON_QUERY(evidence) <> '[]')");
                    table.CheckConstraint("ck_certificate_instance_status", "status IN ('draft', 'issued', 'superseded')");
                    table.CheckConstraint("ck_certificate_instance_unknowns_json", "ISJSON(unknowns) = 1");
                    table.ForeignKey(
                        name: "fk_certificate_instance_tenant_id_supersedes_certificate_id",
                        columns: x => new { x.tenant_id, x.supersedes_certificate_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_instance",
                        principalColumns: new[] { "tenant_id", "certificate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_certificate_instance_tenant_id_template_version_id",
                        columns: x => new { x.tenant_id, x.template_version_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_template_version",
                        principalColumns: new[] { "tenant_id", "template_version_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "certificate_trigger",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    triggering_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    outcome = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    refusal_rule = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    refusal_detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_trigger", x => new { x.tenant_id, x.unit_id, x.document_kind, x.triggering_event_id });
                    table.CheckConstraint("ck_certificate_trigger_document_kind", "document_kind IN ('release_record', 'warranty_certificate')");
                    table.CheckConstraint("ck_certificate_trigger_outcome", "outcome IN ('issued', 'absorbed', 'refused')");
                    table.ForeignKey(
                        name: "fk_certificate_trigger_tenant_id_certificate_id",
                        columns: x => new { x.tenant_id, x.certificate_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_instance",
                        principalColumns: new[] { "tenant_id", "certificate_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "certificate_signature",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    signature_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    signer_identity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    printed_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    meaning = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    signed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    hash_signed = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    signature_method = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_signature", x => new { x.tenant_id, x.signature_id });
                    table.CheckConstraint("ck_certificate_signature_meaning", "meaning IN ('release', 'review', 'approval', 'authorship')");
                    table.ForeignKey(
                        name: "fk_certificate_signature_tenant_id_certificate_id",
                        columns: x => new { x.tenant_id, x.certificate_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_instance",
                        principalColumns: new[] { "tenant_id", "certificate_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reprint_log",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reprint_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    requested_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    recipient_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reprint_log", x => new { x.tenant_id, x.reprint_id });
                    table.ForeignKey(
                        name: "fk_reprint_log_tenant_id_certificate_id",
                        columns: x => new { x.tenant_id, x.certificate_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_instance",
                        principalColumns: new[] { "tenant_id", "certificate_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_document_kind_form_type_status_issued_at",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "document_kind", "form_type", "status", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_supersedes_certificate_id",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "supersedes_certificate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_template_version_id",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "template_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_unit_id_document_kind_issued_at",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "unit_id", "document_kind", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ux_certificate_instance_tenant_id_tracking_number",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "tracking_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_certificate_instance_tenant_id_unit_id_document_kind_triggering_event_id",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "unit_id", "document_kind", "triggering_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_certificate_instance_tenant_id_unit_id_document_kind_version",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "unit_id", "document_kind", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_certificate_trigger_tenant_id_certificate_id",
                schema: "serialisation",
                table: "certificate_trigger",
                columns: new[] { "tenant_id", "certificate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_signature_tenant_id_certificate_id_signed_at",
                schema: "serialisation",
                table: "certificate_signature",
                columns: new[] { "tenant_id", "certificate_id", "signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_template_tenant_id_document_kind_form_type_applies_to_item_class",
                schema: "serialisation",
                table: "certificate_template",
                columns: new[] { "tenant_id", "document_kind", "form_type", "applies_to_item_class" });

            migrationBuilder.CreateIndex(
                name: "ux_certificate_template_tenant_id_code",
                schema: "serialisation",
                table: "certificate_template",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_certificate_template_version_tenant_id_template_id_status_effective_from",
                schema: "serialisation",
                table: "certificate_template_version",
                columns: new[] { "tenant_id", "template_id", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_certificate_template_version_tenant_id_template_id_version",
                schema: "serialisation",
                table: "certificate_template_version",
                columns: new[] { "tenant_id", "template_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reprint_log_tenant_id_certificate_id_at",
                schema: "serialisation",
                table: "reprint_log",
                columns: new[] { "tenant_id", "certificate_id", "at" });

            migrationBuilder.AddTenantPolicy("serialisation", "certificate_template");
            migrationBuilder.AddTenantPolicy("serialisation", "certificate_template_version");
            migrationBuilder.AddTenantPolicy("serialisation", "certificate_instance");
            migrationBuilder.AddTenantPolicy("serialisation", "certificate_trigger");
            migrationBuilder.AddTenantPolicy("serialisation", "certificate_signature");
            migrationBuilder.AddTenantPolicy("serialisation", "reprint_log");

            migrationBuilder.Sql("""
                CREATE TRIGGER [serialisation].[tr_certificate_instance_immutable]
                ON [serialisation].[certificate_instance]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                    BEGIN
                        THROW 51000, 'Certificate versions cannot be deleted.', 1;
                    END;
                    IF UPDATE(tenant_id) OR UPDATE(certificate_id) OR UPDATE(unit_id)
                       OR UPDATE(document_kind) OR UPDATE(form_type) OR UPDATE(version)
                       OR UPDATE(tracking_number) OR UPDATE(template_version_id)
                       OR UPDATE(as_built_revision_id) OR UPDATE(payload) OR UPDATE(payload_hash) OR UPDATE(facts_hash)
                       OR UPDATE(supersedes_certificate_id) OR UPDATE(supersede_reason)
                       OR UPDATE(triggering_event_id) OR UPDATE(issuer_entity_id)
                       OR UPDATE(issuer_snapshot) OR UPDATE(owner_snapshot) OR UPDATE(branding_snapshot)
                       OR UPDATE(language) OR UPDATE(provenance) OR UPDATE(unknowns) OR UPDATE(evidence)
                       OR UPDATE(issued_at)
                    BEGIN
                        THROW 51000, 'Certificate versions are immutable; only issued-to-superseded status is allowed.', 1;
                    END;
                    IF EXISTS (
                        SELECT 1 FROM inserted i
                        JOIN deleted d ON d.tenant_id = i.tenant_id AND d.certificate_id = i.certificate_id
                        WHERE d.status <> 'issued' OR i.status <> 'superseded')
                    BEGIN
                        THROW 51000, 'Certificate status may only change from issued to superseded.', 1;
                    END;
                END
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER [serialisation].[tr_certificate_signature_immutable]
                ON [serialisation].[certificate_signature]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Certificate signatures are immutable and cannot be transferred or excised.', 1;
                END
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER [serialisation].[tr_certificate_template_version_immutable]
                ON [serialisation].[certificate_template_version]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                       AND EXISTS (SELECT 1 FROM deleted WHERE status <> 'draft')
                    BEGIN
                        THROW 51000, 'An activated certificate template version cannot be deleted.', 1;
                    END;
                    IF EXISTS (
                        SELECT 1 FROM inserted i
                        JOIN deleted d ON d.tenant_id = i.tenant_id AND d.template_version_id = i.template_version_id
                        WHERE d.status <> 'draft'
                          AND NOT (d.status = 'active' AND i.status = 'retired'
                                   AND i.effective_to IS NOT NULL
                                   AND i.template_id = d.template_id
                                   AND i.version = d.version
                                   AND i.language = d.language
                                   AND i.payload_schema_version = d.payload_schema_version
                                   AND i.layout_kind = d.layout_kind
                                   AND i.layout_body = d.layout_body
                                   AND i.page_size = d.page_size
                                   AND i.orientation = d.orientation
                                   AND i.margin_top_mm = d.margin_top_mm
                                   AND i.margin_right_mm = d.margin_right_mm
                                   AND i.margin_bottom_mm = d.margin_bottom_mm
                                   AND i.margin_left_mm = d.margin_left_mm
                                   AND ISNULL(i.header_body, '') = ISNULL(d.header_body, '')
                                   AND ISNULL(i.footer_body, '') = ISNULL(d.footer_body, '')
                                   AND ISNULL(i.branding_asset_ref, '') = ISNULL(d.branding_asset_ref, '')
                                   AND i.static_text = d.static_text
                                   AND i.fields = d.fields
                                   AND ISNULL(i.approved_by, '') = ISNULL(d.approved_by, '')
                                   AND ISNULL(i.approved_at, '0001-01-01') = ISNULL(d.approved_at, '0001-01-01')
                                   AND i.created_at = d.created_at
                                   AND i.created_by = d.created_by))
                    BEGIN
                        THROW 51000, 'An activated certificate template version is immutable.', 1;
                    END;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "reprint_log");
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_signature");
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_trigger");
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_instance");
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_template_version");
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_template");

            migrationBuilder.DropTable(
                name: "certificate_signature",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "reprint_log",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "certificate_trigger",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "certificate_instance",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "certificate_template_version",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "certificate_template",
                schema: "serialisation");
        }
    }
}
