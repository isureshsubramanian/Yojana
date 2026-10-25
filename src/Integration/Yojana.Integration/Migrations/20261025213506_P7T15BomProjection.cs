using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P7T15BomProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bom_projection",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    bom_projection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_item_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    source_changed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    deleted = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bom_projection", x => new { x.tenant_id, x.bom_projection_id });
                });

            migrationBuilder.CreateTable(
                name: "bom_projection_line",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    bom_projection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    line_no = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    item_external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    quantity = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    uom = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bom_projection_line", x => new { x.tenant_id, x.bom_projection_id, x.line_no });
                    table.CheckConstraint("ck_bom_projection_line_kind", "kind IN ('item','resource','text','other')");
                    table.ForeignKey(
                        name: "fk_bom_projection_line_tenant_id_bom_projection_id",
                        columns: x => new { x.tenant_id, x.bom_projection_id },
                        principalSchema: "integration",
                        principalTable: "bom_projection",
                        principalColumns: new[] { "tenant_id", "bom_projection_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bom_projection_tenant_id_connection_id_parent_item_external_id",
                schema: "integration",
                table: "bom_projection",
                columns: new[] { "tenant_id", "connection_id", "parent_item_external_id" });

            migrationBuilder.CreateIndex(
                name: "ux_bom_projection_tenant_id_parent_item_id",
                schema: "integration",
                table: "bom_projection",
                columns: new[] { "tenant_id", "parent_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bom_projection_line_tenant_id_item_id_bom_projection_id",
                schema: "integration",
                table: "bom_projection_line",
                columns: new[] { "tenant_id", "item_id", "bom_projection_id" });

            // The staged insert must see every tenant's existing CX01 connection. Keep the shared policy off only
            // inside this migration transaction, add both new predicates, seed in Express-safe batches, then restore it.
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = OFF)");
            migrationBuilder.AddTenantPolicy("integration", "bom_projection");
            migrationBuilder.AddTenantPolicy("integration", "bom_projection_line");
            migrationBuilder.Sql("""
                WHILE 1 = 1
                BEGIN
                    INSERT INTO [integration].[connection_poll_watermark]
                        ([tenant_id], [connection_id], [entity_type], [canonical_message], [watermark],
                         [overlap_seconds], [interval_seconds], [next_poll_at], [last_polled_at], [last_error],
                         [last_error_code], [revision])
                    SELECT TOP (1000) [connection].[tenant_id], [connection].[connection_id],
                           N'product_trees', N'BomUpserted', NULL, 300, 900, SYSUTCDATETIME(), NULL, NULL, NULL, 0
                    FROM [integration].[connection] AS [connection]
                    WHERE [connection].[connector_code] = N'CX01'
                      AND NOT EXISTS (
                          SELECT 1
                          FROM [integration].[connection_poll_watermark] AS [poll]
                          WHERE [poll].[tenant_id] = [connection].[tenant_id]
                            AND [poll].[connection_id] = [connection].[connection_id]
                            AND [poll].[entity_type] = N'product_trees');
                    IF @@ROWCOUNT = 0 BREAK;
                END
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = ON)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = OFF)");
            migrationBuilder.Sql("""
                DELETE FROM [integration].[connection_poll_watermark]
                WHERE [entity_type] = N'product_trees' AND [canonical_message] = N'BomUpserted';
                """);
            migrationBuilder.RemoveTenantPolicy("integration", "bom_projection_line");
            migrationBuilder.RemoveTenantPolicy("integration", "bom_projection");

            migrationBuilder.DropTable(
                name: "bom_projection_line",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "bom_projection",
                schema: "integration");

            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = ON)");
        }
    }
}
