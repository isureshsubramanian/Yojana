using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T19CoverItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "occurred_date",
                schema: "serialisation",
                table: "claim",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reported_cause_code",
                schema: "serialisation",
                table: "claim",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reported_description",
                schema: "serialisation",
                table: "claim",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "claim_cause",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    customer_wording = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "bit", nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_cause", x => new { x.tenant_id, x.code });
                });

            migrationBuilder.CreateTable(
                name: "policy_cover_item",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    type = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    customer_wording = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    internal_note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    category = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    parts_scope = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_cover_item", x => new { x.tenant_id, x.policy_version_id, x.code });
                    table.CheckConstraint("ck_policy_cover_item_category", "category IS NULL OR category IN ('cause','part','condition','service')");
                    table.CheckConstraint("ck_policy_cover_item_scope", "ISJSON(parts_scope) = 1");
                    table.CheckConstraint("ck_policy_cover_item_type", "type IN ('covered','excluded')");
                    table.ForeignKey(
                        name: "fk_policy_cover_item_tenant_id_policy_version_id",
                        columns: x => new { x.tenant_id, x.policy_version_id },
                        principalSchema: "serialisation",
                        principalTable: "warranty_policy_version",
                        principalColumns: new[] { "tenant_id", "policy_version_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "policy_cover_item_cause",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    cause_code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_cover_item_cause", x => new { x.tenant_id, x.policy_version_id, x.item_code, x.cause_code });
                    table.ForeignKey(
                        name: "fk_policy_cover_item_cause_tenant_id_cause_code",
                        columns: x => new { x.tenant_id, x.cause_code },
                        principalSchema: "serialisation",
                        principalTable: "claim_cause",
                        principalColumns: new[] { "tenant_id", "code" });
                    table.ForeignKey(
                        name: "fk_policy_cover_item_cause_tenant_id_policy_version_id_item_code",
                        columns: x => new { x.tenant_id, x.policy_version_id, x.item_code },
                        principalSchema: "serialisation",
                        principalTable: "policy_cover_item",
                        principalColumns: new[] { "tenant_id", "policy_version_id", "code" },
                        onDelete: ReferentialAction.Cascade);
                });

            // The nullable claim FK and tenant-led index still scan the existing claim table during upgrade.
            migrationBuilder.CreateIndex(
                name: "ix_policy_cover_item_tenant_id_code",
                schema: "serialisation",
                table: "policy_cover_item",
                columns: new[] { "tenant_id", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_reported_cause_code",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "reported_cause_code" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_cover_item_cause_tenant_id_cause_code",
                schema: "serialisation",
                table: "policy_cover_item_cause",
                columns: new[] { "tenant_id", "cause_code" });

            migrationBuilder.AddForeignKey(
                name: "fk_claim_tenant_id_reported_cause_code",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "reported_cause_code" },
                principalSchema: "serialisation",
                principalTable: "claim_cause",
                principalColumns: new[] { "tenant_id", "code" });
            migrationBuilder.AddTenantPolicy("serialisation", "claim_cause");
            migrationBuilder.AddTenantPolicy("serialisation", "policy_cover_item");
            migrationBuilder.AddTenantPolicy("serialisation", "policy_cover_item_cause");
            migrationBuilder.Sql(LegacyBackfill);
        }

        // Coupled to WarrantyPolicyVersion.Exclusions: the JSON column stays read-only for one release.
        // Drop it only after legacy readers move to policy_cover_item; this backfill must retain the sold version.
        // Enumerate only this module's tenants with RLS OFF/ON in one XACT_ABORT transaction. Cancellation rolls
        // the policy state back to ON. The schema and backfill share EF's migration transaction (no suppression).
        // Standalone re-runs open their own transaction for enumeration, then insert at most 500 items per batch
        // with RLS ON in each tenant context. Invalid JSON rows are skipped and counted; blank strings are skipped.
        // Scope JSON uses PolicyCoverItems.Read's snake-case contract; SQL cannot invoke its serialiser.
        // NOT EXISTS makes re-running safe. Long legacy text is retained whole; only new or changed wording is limited.
        public const string LegacyBackfill = """
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            CREATE TABLE #legacy_tenants (tenant_id uniqueidentifier NOT NULL PRIMARY KEY);
            DECLARE @original_tenant sql_variant = SESSION_CONTEXT(N'tenant_id');
            DECLARE @own_transaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
            DECLARE @invalid_json int;
            DECLARE @whitespace nvarchar(32) = NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)
                +NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)
                +NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)
                +NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
            BEGIN TRY
                IF @own_transaction = 1 BEGIN TRANSACTION;
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = OFF);
                INSERT INTO #legacy_tenants SELECT DISTINCT tenant_id FROM serialisation.warranty_policy_version;
                SELECT @invalid_json = COUNT(*) FROM serialisation.warranty_policy_version WHERE ISJSON(exclusions) <> 1;
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);
                IF @own_transaction = 1 COMMIT TRANSACTION;
                PRINT CONCAT(N'Legacy exclusions: skipped invalid JSON rows: ', @invalid_json);
                DECLARE @tenant uniqueidentifier, @inserted int;
                WHILE EXISTS (SELECT 1 FROM #legacy_tenants)
                BEGIN
                    SELECT TOP(1) @tenant = tenant_id FROM #legacy_tenants ORDER BY tenant_id;
                    EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@tenant;
                    SET @inserted = 1;
                    WHILE @inserted > 0
                    BEGIN
                        INSERT INTO serialisation.policy_cover_item (tenant_id, policy_version_id, code, type, customer_wording, category, parts_scope)
                        SELECT TOP(500) p.tenant_id, p.policy_version_id, CONCAT(N'LEGACY-', CONVERT(int, j.[key]) + 1),
                            N'excluded', j.[value], NULL, N'{"part_categories":[],"item_families":[]}'
                        FROM serialisation.warranty_policy_version p
                        CROSS APPLY OPENJSON(CASE WHEN ISJSON(p.exclusions) = 1 THEN p.exclusions ELSE N'[]' END) j
                        WHERE p.tenant_id = @tenant AND j.[type] = 1
                            AND DATALENGTH(TRIM(@whitespace FROM j.[value])) > 0
                            AND NOT EXISTS (SELECT 1 FROM serialisation.policy_cover_item i
                                WHERE i.tenant_id = p.tenant_id AND i.policy_version_id = p.policy_version_id
                                    AND i.code = CONCAT(N'LEGACY-', CONVERT(int, j.[key]) + 1))
                        ORDER BY p.policy_version_id, CONVERT(int, j.[key])
                        OPTION(MAXDOP 1);
                        SET @inserted = @@ROWCOUNT;
                    END;
                    DELETE FROM #legacy_tenants WHERE tenant_id = @tenant;
                END;
                EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@original_tenant;
            END TRY
            BEGIN CATCH
                IF @own_transaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@original_tenant;
                THROW;
            END CATCH;
            DROP TABLE #legacy_tenants;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "policy_cover_item_cause");
            migrationBuilder.RemoveTenantPolicy("serialisation", "policy_cover_item");
            migrationBuilder.RemoveTenantPolicy("serialisation", "claim_cause");
            migrationBuilder.DropForeignKey(
                name: "fk_claim_tenant_id_reported_cause_code",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropTable(
                name: "policy_cover_item_cause",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "claim_cause",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "policy_cover_item",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_claim_tenant_id_reported_cause_code",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "occurred_date",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "reported_cause_code",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "reported_description",
                schema: "serialisation",
                table: "claim");
        }
    }
}
