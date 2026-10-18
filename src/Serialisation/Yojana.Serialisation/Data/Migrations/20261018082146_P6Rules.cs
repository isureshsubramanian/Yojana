using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6Rules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "region_group",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    region_group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    last_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    members = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    last_modified_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_modified_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_region_group", x => new { x.tenant_id, x.region_group_id });
                    table.CheckConstraint("ck_region_group_members_json", "ISJSON(members) = 1");
                });

            migrationBuilder.CreateTable(
                name: "rule_pack",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_pack_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    warning = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    built_in = table.Column<bool>(type: "bit", nullable: false),
                    import_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    imported_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    imported_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_pack", x => new { x.tenant_id, x.rule_pack_id });
                });

            migrationBuilder.CreateTable(
                name: "jurisdiction_rule",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    root_rule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supersedes_rule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    scope = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    country_code = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    subdivision_code = table.Column<string>(type: "varchar(7)", unicode: false, maxLength: 7, nullable: true),
                    region_group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    buyer_class = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    item_class = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    price_min = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    price_max = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    date_tested = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    source_citation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    source_note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    confirmed_by_counsel = table.Column<bool>(type: "bit", nullable: false),
                    counsel_confirmed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    approved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    closed_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    closed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jurisdiction_rule", x => new { x.tenant_id, x.rule_id });
                    table.CheckConstraint("ck_jurisdiction_rule_buyer", "buyer_class IN ('any', 'consumer', 'business')");
                    table.CheckConstraint("ck_jurisdiction_rule_currency", "(price_min IS NULL AND price_max IS NULL) OR currency IS NOT NULL");
                    table.CheckConstraint("ck_jurisdiction_rule_date_tested", "date_tested IN ('contract', 'purchase', 'delivery', 'first_sale')");
                    table.CheckConstraint("ck_jurisdiction_rule_effective", "effective_to IS NULL OR effective_to > effective_from");
                    table.CheckConstraint("ck_jurisdiction_rule_price", "price_min IS NULL OR price_max IS NULL OR price_max >= price_min");
                    table.CheckConstraint("ck_jurisdiction_rule_scope", "scope IN ('any', 'country', 'subdivision', 'region_group')");
                    table.CheckConstraint("ck_jurisdiction_rule_scope_fields", "(scope = 'any' AND country_code IS NULL AND subdivision_code IS NULL AND region_group_id IS NULL) OR (scope = 'country' AND country_code IS NOT NULL AND subdivision_code IS NULL AND region_group_id IS NULL) OR (scope = 'subdivision' AND country_code IS NULL AND subdivision_code IS NOT NULL AND region_group_id IS NULL) OR (scope = 'region_group' AND country_code IS NULL AND subdivision_code IS NULL AND region_group_id IS NOT NULL)");
                    table.CheckConstraint("ck_jurisdiction_rule_value_json", "ISJSON(value, VALUE) = 1");
                    table.ForeignKey(
                        name: "fk_jurisdiction_rule_tenant_id_region_group_id",
                        columns: x => new { x.tenant_id, x.region_group_id },
                        principalSchema: "serialisation",
                        principalTable: "region_group",
                        principalColumns: new[] { "tenant_id", "region_group_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rule_pack_rule",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_pack_rule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_pack_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    template = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_pack_rule", x => new { x.tenant_id, x.rule_pack_rule_id });
                    table.CheckConstraint("ck_rule_pack_rule_template_json", "ISJSON(template) = 1");
                    table.ForeignKey(
                        name: "fk_rule_pack_rule_tenant_id_rule_pack_id",
                        columns: x => new { x.tenant_id, x.rule_pack_id },
                        principalSchema: "serialisation",
                        principalTable: "rule_pack",
                        principalColumns: new[] { "tenant_id", "rule_pack_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jurisdiction_rule_tenant_id_kind_scope_country_code_subdivision_code_region_group_id_effective_from",
                schema: "serialisation",
                table: "jurisdiction_rule",
                columns: new[] { "tenant_id", "kind", "scope", "country_code", "subdivision_code", "region_group_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_jurisdiction_rule_tenant_id_region_group_id",
                schema: "serialisation",
                table: "jurisdiction_rule",
                columns: new[] { "tenant_id", "region_group_id" });

            migrationBuilder.CreateIndex(
                name: "ux_jurisdiction_rule_tenant_id_closed_command_id",
                schema: "serialisation",
                table: "jurisdiction_rule",
                columns: new[] { "tenant_id", "closed_command_id" },
                unique: true,
                filter: "[closed_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_jurisdiction_rule_tenant_id_command_id",
                schema: "serialisation",
                table: "jurisdiction_rule",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_jurisdiction_rule_tenant_id_root_rule_id_version",
                schema: "serialisation",
                table: "jurisdiction_rule",
                columns: new[] { "tenant_id", "root_rule_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_region_group_tenant_id_name",
                schema: "serialisation",
                table: "region_group",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_region_group_tenant_id_code",
                schema: "serialisation",
                table: "region_group",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_region_group_tenant_id_last_command_id",
                schema: "serialisation",
                table: "region_group",
                columns: new[] { "tenant_id", "last_command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rule_pack_tenant_id_code",
                schema: "serialisation",
                table: "rule_pack",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rule_pack_tenant_id_import_command_id",
                schema: "serialisation",
                table: "rule_pack",
                columns: new[] { "tenant_id", "import_command_id" },
                unique: true,
                filter: "[import_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_rule_pack_rule_tenant_id_rule_pack_id_sequence",
                schema: "serialisation",
                table: "rule_pack_rule",
                columns: new[] { "tenant_id", "rule_pack_id", "sequence" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "region_group");

            // Compatibility coupling: policy region_scope and refurbished-policy JSON written before this catalogue
            // use EU, EEA, EFTA and UK as stable tokens. These tenant-owned groups preserve that meaning; no legal rule
            // value is backfilled. Removing this insert makes existing policies silently lose geographic coverage.
            // Row-level security hides every tenant from a migration session, so it is disabled for this statement only.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                -- migration:legacy-region-scopes
                INSERT INTO [serialisation].[region_group]
                    ([tenant_id], [region_group_id], [last_command_id], [code], [name], [members],
                     [created_at], [created_by], [last_modified_at], [last_modified_by])
                SELECT tenants.[tenant_id], NEWID(), NEWID(), definitions.[code], definitions.[name],
                       definitions.[members], SYSUTCDATETIME(), N'migration:P6Rules', SYSUTCDATETIME(), N'migration:P6Rules'
                FROM (SELECT DISTINCT [tenant_id] FROM [serialisation].[warranty_policy_version]) AS tenants
                CROSS JOIN (VALUES
                    ('EU', N'European Union (starter — confirm with your legal adviser)',
                        N'["AT","BE","BG","CY","CZ","DE","DK","EE","ES","FI","FR","GR","HR","HU","IE","IT","LT","LU","LV","MT","NL","PL","PT","RO","SE","SI","SK"]'),
                    ('EEA', N'European Economic Area (starter — confirm with your legal adviser)',
                        N'["AT","BE","BG","CY","CZ","DE","DK","EE","ES","FI","FR","GR","HR","HU","IE","IS","IT","LI","LT","LU","LV","MT","NL","NO","PL","PT","RO","SE","SI","SK"]'),
                    ('EFTA', N'European Free Trade Association (starter — confirm with your legal adviser)',
                        N'["CH","IS","LI","NO"]'),
                    ('UK', N'United Kingdom (starter — confirm with your legal adviser)', N'["GB"]')
                ) AS definitions([code], [name], [members])
                WHERE NOT EXISTS (
                    SELECT 1 FROM [serialisation].[region_group] existing
                    WHERE existing.[tenant_id] = tenants.[tenant_id] AND existing.[code] = definitions.[code]);
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");
            migrationBuilder.AddTenantPolicy("serialisation", "jurisdiction_rule");
            migrationBuilder.AddTenantPolicy("serialisation", "rule_pack");
            migrationBuilder.AddTenantPolicy("serialisation", "rule_pack_rule");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "rule_pack_rule");
            migrationBuilder.RemoveTenantPolicy("serialisation", "rule_pack");
            migrationBuilder.RemoveTenantPolicy("serialisation", "jurisdiction_rule");
            migrationBuilder.RemoveTenantPolicy("serialisation", "region_group");

            migrationBuilder.DropTable(
                name: "jurisdiction_rule",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "rule_pack_rule",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "region_group",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "rule_pack",
                schema: "serialisation");
        }
    }
}
