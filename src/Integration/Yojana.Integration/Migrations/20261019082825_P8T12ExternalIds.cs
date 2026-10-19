using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T12ExternalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The migration is the one authorised cross-tenant operation: RLS would otherwise hide every existing
            // reference row from the data move. The policy is disabled only inside this migration transaction.
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = OFF)");
            migrationBuilder.CreateTable(
                name: "connector_catalogue",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    customer_cost_disclosure = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connector_catalogue", x => new { x.tenant_id, x.connector_code });
                });

            migrationBuilder.CreateTable(
                name: "external_id_map",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    external_id_map_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    module_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    valid_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    valid_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    natural_key_hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    mapping_source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_id_map", x => new { x.tenant_id, x.external_id_map_id });
                    table.CheckConstraint("ck_external_id_map_source", "mapping_source IN ('manual','auto','migration')");
                });

            migrationBuilder.Sql("""
                INSERT INTO [integration].[external_id_map]
                    ([tenant_id], [external_id_map_id], [connection_id], [entity_type], [external_id], [module_ref],
                     [valid_from], [valid_to], [natural_key_hash], [mapping_source])
                SELECT [tenant_id], NEWID(), [connection_id], [entity_type], [external_id], [module_id],
                       [updated_at], NULL,
                       LOWER(CONVERT(char(64), HASHBYTES('SHA2_256', UPPER([entity_type]) + CHAR(10) + UPPER([external_id])), 2)),
                       'migration'
                FROM [integration].[reference_projection];

                DELETE FROM [integration].[reference_projection]
                WHERE [entity_type] NOT IN ('item', 'party');
                """);

            migrationBuilder.DropPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropIndex(
                name: "ix_reference_projection_tenant_id_module_id_entity_type",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropColumn(
                name: "connection_id",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropColumn(
                name: "external_id",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropColumn(
                name: "provisional",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.AddColumn<string>(
                name: "attributes_json",
                schema: "integration",
                table: "reference_projection",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "entity_type", "module_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reference_projection_tenant_id_entity_type_code",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "entity_type", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_connector_catalogue_tenant_id_enabled_display_name_connector_code",
                schema: "integration",
                table: "connector_catalogue",
                columns: new[] { "tenant_id", "enabled", "display_name", "connector_code" });

            migrationBuilder.CreateIndex(
                name: "ix_external_id_map_tenant_id_module_ref_entity_type_valid_to",
                schema: "integration",
                table: "external_id_map",
                columns: new[] { "tenant_id", "module_ref", "entity_type", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "ux_external_id_map_tenant_id_connection_id_entity_type_external_id",
                schema: "integration",
                table: "external_id_map",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "external_id" },
                unique: true,
                filter: "[valid_to] IS NULL");

            migrationBuilder.AddTenantPolicy("integration", "connector_catalogue");
            migrationBuilder.AddTenantPolicy("integration", "external_id_map");
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = ON)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connector_catalogue");
            migrationBuilder.RemoveTenantPolicy("integration", "external_id_map");
            migrationBuilder.DropTable(
                name: "connector_catalogue",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "external_id_map",
                schema: "integration");

            migrationBuilder.DropPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropIndex(
                name: "ix_reference_projection_tenant_id_entity_type_code",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropColumn(
                name: "attributes_json",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.AddColumn<Guid>(
                name: "connection_id",
                schema: "integration",
                table: "reference_projection",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                schema: "integration",
                table: "reference_projection",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "provisional",
                schema: "integration",
                table: "reference_projection",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "external_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reference_projection_tenant_id_module_id_entity_type",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "module_id", "entity_type" });
        }
    }
}
