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
                    customer_wording = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
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
            migrationBuilder.Sql(LegacyBackfill, suppressTransaction: true);
        }

        // Coupled to WarrantyPolicyVersion.Exclusions: the JSON column stays read-only for one release.
        // Drop it only after legacy readers move to policy_cover_item; this backfill must retain the sold version.
        // Stage once with RLS briefly disabled, restore it, then insert 500 rows at a time in each tenant context.
        // Scope JSON uses PolicyCoverItems.Read's snake-case contract; SQL cannot invoke its serialiser.
        // Changing those keys without migrating saved scopes would discard their part references.
        // NOT EXISTS makes re-running the data step safe; customer text and list order are never inferred or changed.
        public const string LegacyBackfill = """
            SET NOCOUNT ON;
            CREATE TABLE #legacy_items (tenant_id uniqueidentifier NOT NULL, policy_version_id uniqueidentifier NOT NULL,
                code nvarchar(24) NOT NULL, customer_wording nvarchar(max) NOT NULL,
                PRIMARY KEY(tenant_id, policy_version_id, code));
            DECLARE @original_tenant sql_variant = SESSION_CONTEXT(N'tenant_id');
            BEGIN TRY
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = OFF);
                INSERT INTO #legacy_items
                SELECT p.tenant_id, p.policy_version_id, CONCAT(N'LEGACY-', CONVERT(int, j.[key]) + 1), j.[value]
                FROM serialisation.warranty_policy_version p CROSS APPLY OPENJSON(p.exclusions) j
                WHERE j.[type] = 1
                OPTION(MAXDOP 1);
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);
                IF EXISTS (SELECT 1 FROM #legacy_items WHERE DATALENGTH(customer_wording) > 1000)
                    THROW 51000, 'Legacy exclusion exceeds 500 characters; owner review is required before migration.', 1;
                DECLARE @tenant uniqueidentifier;
                WHILE EXISTS (SELECT 1 FROM #legacy_items)
                BEGIN
                    SELECT TOP(1) @tenant = tenant_id FROM #legacy_items ORDER BY tenant_id;
                    EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@tenant;
                    INSERT INTO serialisation.policy_cover_item (tenant_id, policy_version_id, code, type, customer_wording, category, parts_scope)
                    SELECT TOP(500) s.tenant_id, s.policy_version_id, s.code, N'excluded', s.customer_wording, NULL,
                        N'{"part_categories":[],"item_families":[]}'
                    FROM #legacy_items s
                    WHERE s.tenant_id = @tenant AND NOT EXISTS (SELECT 1 FROM serialisation.policy_cover_item i
                        WHERE i.tenant_id = s.tenant_id AND i.policy_version_id = s.policy_version_id AND i.code = s.code)
                    ORDER BY s.policy_version_id, s.code;
                    DELETE s FROM #legacy_items s WHERE s.tenant_id = @tenant AND EXISTS (SELECT 1 FROM serialisation.policy_cover_item i
                        WHERE i.tenant_id = s.tenant_id AND i.policy_version_id = s.policy_version_id AND i.code = s.code);
                END;
                EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@original_tenant;
            END TRY
            BEGIN CATCH
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);
                EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@original_tenant;
                THROW;
            END CATCH;
            DROP TABLE #legacy_items;
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
