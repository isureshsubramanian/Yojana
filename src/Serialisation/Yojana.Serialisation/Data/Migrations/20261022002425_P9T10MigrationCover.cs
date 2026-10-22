using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T10MigrationCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.AlterColumn<Guid>(
                name: "warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "source_entitlement_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "migration_load_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "migration_load_id",
                schema: "serialisation",
                table: "entitlement",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "migration_load_id",
                schema: "serialisation",
                table: "certificate_instance",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "entitlement_evidence",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    migration_load_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    termination_date = table.Column<DateOnly>(type: "date", nullable: true),
                    direction = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    selected = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_evidence", x => new { x.tenant_id, x.evidence_id });
                    table.ForeignKey(
                        name: "fk_entitlement_evidence_tenant_id_entitlement_id",
                        columns: x => new { x.tenant_id, x.entitlement_id },
                        principalSchema: "serialisation",
                        principalTable: "entitlement",
                        principalColumns: new[] { "tenant_id", "entitlement_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_migration_load_id_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "migration_load_id", "replacement_link_id" },
                filter: "[migration_load_id] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link",
                sql: "source_kind IN ('claim', 'rma', 'loan', 'migrated')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_replacement_link_source_relationship",
                schema: "serialisation",
                table: "replacement_link",
                sql: "(source_kind = 'migrated' AND warranty_replacement_id IS NULL AND source_entitlement_id IS NULL AND migration_load_id IS NOT NULL) OR (source_kind <> 'migrated' AND warranty_replacement_id IS NOT NULL AND source_entitlement_id IS NOT NULL AND migration_load_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_migration_load_id_unit_id",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "migration_load_id", "unit_id" },
                filter: "[migration_load_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_migration_load_id_certificate_id",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "migration_load_id", "certificate_id" },
                filter: "[migration_load_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_evidence_tenant_id_entitlement_id_source_kind_evidence_id",
                schema: "serialisation",
                table: "entitlement_evidence",
                columns: new[] { "tenant_id", "entitlement_id", "source_kind", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_evidence_tenant_id_migration_load_id_entitlement_id",
                schema: "serialisation",
                table: "entitlement_evidence",
                columns: new[] { "tenant_id", "migration_load_id", "entitlement_id" });

            migrationBuilder.AddTenantPolicy("serialisation", "entitlement_evidence");

            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER [serialisation].[tr_certificate_signature_immutable]
                ON [serialisation].[certificate_signature]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted)
                    BEGIN
                        THROW 51000, 'Certificate signatures are immutable and cannot be transferred or excised.', 1;
                    END;

                    DECLARE @migration_load_id uniqueidentifier = TRY_CONVERT(uniqueidentifier,
                        SESSION_CONTEXT(N'yojana_migration_rollback_load_id'));
                    -- COUPLED to MigrationLoadHandlers rollback: that handler sets this exact session key and deletes
                    -- only signatures whose certificate belongs to the same migrated load. Renaming either side would
                    -- make rehearsal rollback fail closed; widening this predicate would weaken certificate immutability.
                    IF @migration_load_id IS NULL OR EXISTS (
                        SELECT 1
                        FROM deleted d
                        LEFT JOIN [serialisation].[certificate_instance] c
                          ON c.tenant_id = d.tenant_id AND c.certificate_id = d.certificate_id
                        WHERE c.certificate_id IS NULL OR c.provenance <> 'migrated'
                           OR c.migration_load_id IS NULL OR c.migration_load_id <> @migration_load_id)
                    BEGIN
                        THROW 51000, 'Certificate signatures are immutable and cannot be transferred or excised.', 1;
                    END;
                END
                """);

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
                CREATE OR ALTER TRIGGER [serialisation].[tr_certificate_signature_immutable]
                ON [serialisation].[certificate_signature]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Certificate signatures are immutable and cannot be transferred or excised.', 1;
                END
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER [serialisation].[tr_certificate_instance_immutable]
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

            migrationBuilder.RemoveTenantPolicy("serialisation", "entitlement_evidence");

            migrationBuilder.DropTable(
                name: "entitlement_evidence",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_replacement_link_tenant_id_migration_load_id_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropCheckConstraint(
                name: "ck_replacement_link_source_relationship",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ix_entitlement_tenant_id_migration_load_id_unit_id",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropIndex(
                name: "ix_certificate_instance_tenant_id_migration_load_id_certificate_id",
                schema: "serialisation",
                table: "certificate_instance");

            migrationBuilder.DropColumn(
                name: "migration_load_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "migration_load_id",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "migration_load_id",
                schema: "serialisation",
                table: "certificate_instance");

            migrationBuilder.AlterColumn<Guid>(
                name: "warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "source_entitlement_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link",
                sql: "source_kind IN ('claim', 'rma', 'loan')");
        }
    }
}
