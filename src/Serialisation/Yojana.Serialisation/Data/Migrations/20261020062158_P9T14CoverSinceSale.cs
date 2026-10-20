using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T14CoverSinceSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sale_currency",
                schema: "serialisation",
                table: "entitlement",
                type: "char(3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sale_price",
                schema: "serialisation",
                table: "entitlement",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cover_deviation_backfill_checkpoint",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cursor_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    complete = table.Column<bool>(type: "bit", nullable: false),
                    units_processed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cover_deviation_backfill_checkpoint", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "unit_cover_deviation",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sale_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sale_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    sale_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    customer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_family = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    changed = table.Column<bool>(type: "bit", nullable: false),
                    change_count = table.Column<int>(type: "int", nullable: false),
                    added = table.Column<bool>(type: "bit", nullable: false),
                    upgraded = table.Column<bool>(type: "bit", nullable: false),
                    cover_changed = table.Column<bool>(type: "bit", nullable: false),
                    amended = table.Column<bool>(type: "bit", nullable: false),
                    closed_early = table.Column<bool>(type: "bit", nullable: false),
                    suspended = table.Column<bool>(type: "bit", nullable: false),
                    last_change_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_cover_deviation", x => new { x.tenant_id, x.unit_id });
                });

            migrationBuilder.CreateTable(
                name: "unit_cover_deviation_change",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    event_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    months_after_sale = table.Column<int>(type: "int", nullable: false),
                    amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_cover_deviation_change", x => new { x.tenant_id, x.unit_id, x.event_id });
                    table.ForeignKey(
                        name: "fk_unit_cover_deviation_change_tenant_id_unit_id",
                        columns: x => new { x.tenant_id, x.unit_id },
                        principalSchema: "serialisation",
                        principalTable: "unit_cover_deviation",
                        principalColumns: new[] { "tenant_id", "unit_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "unit_cover_deviation_value",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_cover_deviation_value", x => new { x.tenant_id, x.unit_id, x.currency });
                    table.ForeignKey(
                        name: "fk_unit_cover_deviation_value_tenant_id_unit_id",
                        columns: x => new { x.tenant_id, x.unit_id },
                        principalSchema: "serialisation",
                        principalTable: "unit_cover_deviation",
                        principalColumns: new[] { "tenant_id", "unit_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_unit_cover_deviation_tenant_id_changed_last_change_at_unit_id",
                schema: "serialisation",
                table: "unit_cover_deviation",
                columns: new[] { "tenant_id", "changed", "last_change_at", "unit_id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_unit_cover_deviation_tenant_id_customer_party_ref_last_change_at_unit_id",
                schema: "serialisation",
                table: "unit_cover_deviation",
                columns: new[] { "tenant_id", "customer_party_ref", "last_change_at", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_cover_deviation_tenant_id_item_family_last_change_at_unit_id",
                schema: "serialisation",
                table: "unit_cover_deviation",
                columns: new[] { "tenant_id", "item_family", "last_change_at", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_cover_deviation_tenant_id_sale_date_unit_id",
                schema: "serialisation",
                table: "unit_cover_deviation",
                columns: new[] { "tenant_id", "sale_date", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_cover_deviation_change_tenant_id_kind_unit_id_months_after_sale",
                schema: "serialisation",
                table: "unit_cover_deviation_change",
                columns: new[] { "tenant_id", "kind", "unit_id", "months_after_sale" });
            migrationBuilder.AddTenantPolicy("serialisation", "unit_cover_deviation");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_cover_deviation_change");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_cover_deviation_value");
            migrationBuilder.AddTenantPolicy("serialisation", "cover_deviation_backfill_checkpoint");
            // Like the created-event-time migration, stage the ledger once, then commit small keyed updates. RLS is
            // restored before updates; each batch runs in its tenant context. Unrecorded prices stay null.
            migrationBuilder.Sql("""
                SET NOCOUNT ON;
                CREATE TABLE #cover_prices (tenant_id uniqueidentifier NOT NULL, entitlement_id uniqueidentifier NOT NULL,
                    amount decimal(19,4) NOT NULL, currency char(3) NOT NULL, PRIMARY KEY (tenant_id, entitlement_id));
                DECLARE @original_tenant sql_variant = SESSION_CONTEXT(N'tenant_id');
                BEGIN TRY
                    ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = OFF);
                    INSERT INTO #cover_prices
                    SELECT e.tenant_id, e.entitlement_id, p.amount, p.currency
                    FROM serialisation.entitlement e
                    JOIN (
                        SELECT tenant_id, event_id, attributes FROM serialisation.unit_event
                        WHERE event_type IN ('entitlement.extended_sold', 'entitlement.created', 'entitlement.superseded')
                        UNION ALL
                        SELECT tenant_id, event_id, attributes FROM serialisation.unit_event_archive
                        WHERE event_type IN ('entitlement.extended_sold', 'entitlement.created', 'entitlement.superseded')
                    ) f ON f.tenant_id = e.tenant_id AND f.event_id = e.created_event_id
                    CROSS APPLY (SELECT TRY_CONVERT(decimal(19,4), JSON_VALUE(f.attributes, '$.price')) amount,
                        JSON_VALUE(f.attributes, '$.currency') currency) p
                    WHERE e.sale_price IS NULL AND p.amount IS NOT NULL AND LEN(p.currency) = 3
                    OPTION (MAXDOP 1);
                    ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);
                    CREATE TABLE #cover_price_batch (tenant_id uniqueidentifier NOT NULL, entitlement_id uniqueidentifier NOT NULL,
                        amount decimal(19,4) NOT NULL, currency char(3) NOT NULL, PRIMARY KEY (tenant_id, entitlement_id));
                    DECLARE @tenant uniqueidentifier;
                    WHILE EXISTS (SELECT 1 FROM #cover_prices)
                    BEGIN
                        SELECT TOP (1) @tenant = tenant_id FROM #cover_prices ORDER BY tenant_id;
                        EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@tenant;
                        DELETE FROM #cover_price_batch;
                        INSERT INTO #cover_price_batch SELECT TOP (500) * FROM #cover_prices WHERE tenant_id = @tenant ORDER BY entitlement_id;
                        UPDATE e SET sale_price = b.amount, sale_currency = b.currency
                        FROM serialisation.entitlement e JOIN #cover_price_batch b ON b.tenant_id = e.tenant_id AND b.entitlement_id = e.entitlement_id
                        WHERE e.sale_price IS NULL;
                        DELETE p FROM #cover_prices p JOIN #cover_price_batch b ON b.tenant_id = p.tenant_id AND b.entitlement_id = p.entitlement_id;
                    END;
                    EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@original_tenant;
                END TRY
                BEGIN CATCH
                    ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);
                    EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@original_tenant;
                    THROW;
                END CATCH;
                DROP TABLE #cover_price_batch;
                DROP TABLE #cover_prices;
                """, suppressTransaction: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_cover_deviation_change");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_cover_deviation_value");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_cover_deviation");
            migrationBuilder.RemoveTenantPolicy("serialisation", "cover_deviation_backfill_checkpoint");
            migrationBuilder.DropTable(
                name: "cover_deviation_backfill_checkpoint",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_cover_deviation_change",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_cover_deviation_value",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_cover_deviation",
                schema: "serialisation");

            migrationBuilder.DropColumn(
                name: "sale_currency",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "sale_price",
                schema: "serialisation",
                table: "entitlement");
        }
    }
}
