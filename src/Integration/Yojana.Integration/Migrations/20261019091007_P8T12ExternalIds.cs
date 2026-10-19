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
            // This data move must see every tenant's reference rows; the shared policy is disabled only for the
            // migration transaction and is re-enabled after its new tenant predicate has been added.
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = OFF)");

            migrationBuilder.DropPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropIndex(
                name: "ix_reference_projection_tenant_id_module_id_entity_type",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropPrimaryKey(
                name: "pk_external_id_map",
                schema: "integration",
                table: "external_id_map");

            migrationBuilder.AlterColumn<string>(
                name: "natural_key_hash",
                schema: "integration",
                table: "external_id_map",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<Guid>(
                name: "external_id_map_id",
                schema: "integration",
                table: "external_id_map",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [integration].[external_id_map]
                SET [external_id_map_id] = NEWID();

                IF EXISTS (
                    SELECT 1
                    FROM [integration].[reference_projection] AS [reference]
                    INNER JOIN [integration].[external_id_map] AS [mapping]
                        ON [mapping].[tenant_id] = [reference].[tenant_id]
                       AND [mapping].[connection_id] = [reference].[connection_id]
                       AND [mapping].[entity_type] = [reference].[entity_type]
                       AND [mapping].[external_id] = [reference].[external_id]
                       AND [mapping].[valid_to] IS NULL
                    WHERE [mapping].[module_id] <> [reference].[module_id]
                )
                    THROW 51000, 'An active external-id mapping conflicts with a reference projection.', 1;

                INSERT INTO [integration].[external_id_map]
                    ([tenant_id], [external_id_map_id], [connection_id], [entity_type], [external_id], [module_id],
                     [valid_from], [valid_to], [natural_key_hash], [mapping_source], [mapped_by], [mapped_at])
                SELECT [reference].[tenant_id], NEWID(), [reference].[connection_id], [reference].[entity_type],
                       [reference].[external_id], [reference].[module_id], [reference].[updated_at], NULL,
                       LOWER(CONVERT(char(64), HASHBYTES('SHA2_256',
                           UPPER(LTRIM(RTRIM([reference].[entity_type]))) + CHAR(10)
                           + UPPER(LTRIM(RTRIM([reference].[external_id])))), 2)),
                       'migration', 'system:migration:p8t12', [reference].[updated_at]
                FROM [integration].[reference_projection] AS [reference]
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM [integration].[external_id_map] AS [mapping]
                    WHERE [mapping].[tenant_id] = [reference].[tenant_id]
                      AND [mapping].[connection_id] = [reference].[connection_id]
                      AND [mapping].[entity_type] = [reference].[entity_type]
                      AND [mapping].[external_id] = [reference].[external_id]
                      AND [mapping].[valid_to] IS NULL
                );

                DELETE FROM [integration].[reference_projection]
                WHERE [entity_type] NOT IN ('item', 'party');

                WITH [ranked] AS (
                    SELECT *, ROW_NUMBER() OVER (
                        PARTITION BY [tenant_id], [entity_type], [module_id]
                        ORDER BY [updated_at] DESC, [connection_id], [external_id]) AS [row_number]
                    FROM [integration].[reference_projection]
                )
                DELETE FROM [ranked] WHERE [row_number] > 1;
                """);

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

            migrationBuilder.AlterColumn<Guid>(
                name: "external_id_map_id",
                schema: "integration",
                table: "external_id_map",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "entity_type", "module_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_external_id_map",
                schema: "integration",
                table: "external_id_map",
                columns: new[] { "tenant_id", "external_id_map_id" });

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

            migrationBuilder.CreateIndex(
                name: "ix_reference_projection_tenant_id_entity_type_code",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "entity_type", "code" });

            migrationBuilder.CreateIndex(
                name: "ux_external_id_map_tenant_id_connection_id_entity_type_external_id",
                schema: "integration",
                table: "external_id_map",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "external_id" },
                unique: true,
                filter: "[valid_to] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_external_id_map_source",
                schema: "integration",
                table: "external_id_map",
                sql: "mapping_source IN ('auto','migration','outbound','operator','provisional','manual')");

            migrationBuilder.CreateIndex(
                name: "ix_connector_catalogue_tenant_id_enabled_display_name_connector_code",
                schema: "integration",
                table: "connector_catalogue",
                columns: new[] { "tenant_id", "enabled", "display_name", "connector_code" });

            migrationBuilder.AddTenantPolicy("integration", "connector_catalogue");
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = ON)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connector_catalogue");
            migrationBuilder.DropTable(
                name: "connector_catalogue",
                schema: "integration");

            migrationBuilder.DropPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropIndex(
                name: "ix_reference_projection_tenant_id_entity_type_code",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropPrimaryKey(
                name: "pk_external_id_map",
                schema: "integration",
                table: "external_id_map");

            migrationBuilder.DropIndex(
                name: "ux_external_id_map_tenant_id_connection_id_entity_type_external_id",
                schema: "integration",
                table: "external_id_map");

            migrationBuilder.DropCheckConstraint(
                name: "ck_external_id_map_source",
                schema: "integration",
                table: "external_id_map");

            migrationBuilder.DropColumn(
                name: "attributes_json",
                schema: "integration",
                table: "reference_projection");

            migrationBuilder.DropColumn(
                name: "external_id_map_id",
                schema: "integration",
                table: "external_id_map");

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

            migrationBuilder.AlterColumn<string>(
                name: "natural_key_hash",
                schema: "integration",
                table: "external_id_map",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nchar(64)",
                oldFixedLength: true,
                oldMaxLength: 64);

            migrationBuilder.AddPrimaryKey(
                name: "pk_reference_projection",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "external_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_external_id_map",
                schema: "integration",
                table: "external_id_map",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "external_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reference_projection_tenant_id_module_id_entity_type",
                schema: "integration",
                table: "reference_projection",
                columns: new[] { "tenant_id", "module_id", "entity_type" });
        }
    }
}
