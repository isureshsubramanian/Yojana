using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Erase01CertificateRedaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_redaction",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erasure_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    original_payload_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    redacted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_redaction", x => new { x.tenant_id, x.certificate_id });
                    table.ForeignKey(
                        name: "fk_certificate_redaction_tenant_id_certificate_id",
                        columns: x => new { x.tenant_id, x.certificate_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_instance",
                        principalColumns: new[] { "tenant_id", "certificate_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_redaction_tenant_id_erasure_id_certificate_id",
                schema: "serialisation",
                table: "certificate_redaction",
                columns: new[] { "tenant_id", "erasure_id", "certificate_id" });

            migrationBuilder.AddTenantPolicy("serialisation", "certificate_redaction");

            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER [serialisation].[tr_certificate_instance_immutable]
                ON [serialisation].[certificate_instance]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                    BEGIN
                        DECLARE @migration_load_id uniqueidentifier = TRY_CONVERT(uniqueidentifier,
                            SESSION_CONTEXT(N'yojana_migration_rollback_load_id'));
                        -- COUPLED to MigrationLoadHandlers rollback: this is the sole deletion seam, limited to
                        -- migrated certificates from the load named in session context. Separating the key or deletion
                        -- order from that handler would block rollback or expose immutable certificates to deletion.
                        IF @migration_load_id IS NULL OR EXISTS (
                            SELECT 1 FROM deleted
                            WHERE provenance <> 'migrated' OR migration_load_id IS NULL
                               OR migration_load_id <> @migration_load_id)
                        BEGIN
                            THROW 51000, 'Certificate versions cannot be deleted.', 1;
                        END;
                        RETURN;
                    END;

                    DECLARE @erasure_id uniqueidentifier = TRY_CONVERT(uniqueidentifier,
                        SESSION_CONTEXT(N'yojana.erasure_id'));
                    -- TIGHT COUPLING (ADR-0017): SerialisationPersonalDataHolder sets this exact session key because
                    -- the immutable trigger is the final application-write boundary. Renaming either side blocks owner
                    -- redaction; widening this predicate would permit ordinary certificate mutation.
                    IF @erasure_id IS NOT NULL
                       AND NOT EXISTS (
                           SELECT 1 FROM inserted i
                           WHERE NOT EXISTS (
                               SELECT 1 FROM [platform].[erasure_request] e
                               WHERE e.tenant_id = i.tenant_id AND e.erasure_id = @erasure_id
                                 AND e.subject_kind = 'Party' AND e.status IN ('pending','running')))
                    BEGIN
                        IF UPDATE(tenant_id) OR UPDATE(certificate_id) OR UPDATE(unit_id)
                           OR UPDATE(document_kind) OR UPDATE(form_type) OR UPDATE(version)
                           OR UPDATE(tracking_number) OR UPDATE(template_version_id)
                           OR UPDATE(as_built_revision_id) OR UPDATE(payload_hash) OR UPDATE(facts_hash)
                           OR UPDATE(supersedes_certificate_id) OR UPDATE(supersede_reason)
                           OR UPDATE(triggering_event_id) OR UPDATE(issuer_entity_id)
                           OR UPDATE(issuer_snapshot) OR UPDATE(branding_snapshot)
                           OR UPDATE(language) OR UPDATE(provenance) OR UPDATE(unknowns) OR UPDATE(evidence)
                           OR UPDATE(issued_at) OR UPDATE(migration_load_id) OR UPDATE(status)
                        BEGIN
                            THROW 51000, 'An erasure may change only certificate owner_snapshot and payload.', 1;
                        END;
                        RETURN;
                    END;

                    IF UPDATE(tenant_id) OR UPDATE(certificate_id) OR UPDATE(unit_id)
                       OR UPDATE(document_kind) OR UPDATE(form_type) OR UPDATE(version)
                       OR UPDATE(tracking_number) OR UPDATE(template_version_id)
                       OR UPDATE(as_built_revision_id) OR UPDATE(payload) OR UPDATE(payload_hash) OR UPDATE(facts_hash)
                       OR UPDATE(supersedes_certificate_id) OR UPDATE(supersede_reason)
                       OR UPDATE(triggering_event_id) OR UPDATE(issuer_entity_id)
                       OR UPDATE(issuer_snapshot) OR UPDATE(owner_snapshot) OR UPDATE(branding_snapshot)
                       OR UPDATE(language) OR UPDATE(provenance) OR UPDATE(unknowns) OR UPDATE(evidence)
                       OR UPDATE(issued_at) OR UPDATE(migration_load_id)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER [serialisation].[tr_certificate_instance_immutable]
                ON [serialisation].[certificate_instance]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                    BEGIN
                        DECLARE @migration_load_id uniqueidentifier = TRY_CONVERT(uniqueidentifier,
                            SESSION_CONTEXT(N'yojana_migration_rollback_load_id'));
                        IF @migration_load_id IS NULL OR EXISTS (
                            SELECT 1 FROM deleted
                            WHERE provenance <> 'migrated' OR migration_load_id IS NULL
                               OR migration_load_id <> @migration_load_id)
                        BEGIN
                            THROW 51000, 'Certificate versions cannot be deleted.', 1;
                        END;
                        RETURN;
                    END;
                    IF UPDATE(tenant_id) OR UPDATE(certificate_id) OR UPDATE(unit_id)
                       OR UPDATE(document_kind) OR UPDATE(form_type) OR UPDATE(version)
                       OR UPDATE(tracking_number) OR UPDATE(template_version_id)
                       OR UPDATE(as_built_revision_id) OR UPDATE(payload) OR UPDATE(payload_hash) OR UPDATE(facts_hash)
                       OR UPDATE(supersedes_certificate_id) OR UPDATE(supersede_reason)
                       OR UPDATE(triggering_event_id) OR UPDATE(issuer_entity_id)
                       OR UPDATE(issuer_snapshot) OR UPDATE(owner_snapshot) OR UPDATE(branding_snapshot)
                       OR UPDATE(language) OR UPDATE(provenance) OR UPDATE(unknowns) OR UPDATE(evidence)
                       OR UPDATE(issued_at) OR UPDATE(migration_load_id)
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

            migrationBuilder.DropTable(
                name: "certificate_redaction",
                schema: "serialisation");
        }
    }
}
