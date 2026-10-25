using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <summary>
    /// Found 2026-10-26 on the first live bill-of-materials read: a CX01 connection registered before P7-T15 got the
    /// product_trees poll row from P7T15BomProjection but no BomUpserted capability row. The gateway treats a missing
    /// capability as unsupported, so every tree was completed as unsupported and never projected; the content hash
    /// then keeps an unchanged tree from being sent again. This adds the capability a fresh probe declares (native)
    /// and hands those completed-as-unsupported trees back to translation.
    /// </summary>
    public partial class P7T15BomCapabilityBackfill : Migration
    {
        /// <summary>Runs with the tenant policy off in the migration; a database test runs it under one tenant.</summary>
        public const string BackfillSql = """
            WHILE 1 = 1
            BEGIN
                INSERT INTO [integration].[connection_capability]
                    ([tenant_id], [connection_id], [canonical_message], [support_level])
                SELECT TOP (1000) [connection].[tenant_id], [connection].[connection_id], N'BomUpserted', N'native'
                FROM [integration].[connection] AS [connection]
                WHERE [connection].[connector_code] = N'CX01'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM [integration].[connection_capability] AS [capability]
                      WHERE [capability].[tenant_id] = [connection].[tenant_id]
                        AND [capability].[connection_id] = [connection].[connection_id]
                        AND [capability].[canonical_message] = N'BomUpserted');
                IF @@ROWCOUNT = 0 BREAK;
            END

            WHILE 1 = 1
            BEGIN
                UPDATE TOP (1000) [message]
                SET [translation_status] = N'pending', [translated_at] = NULL, [translation_started_at] = NULL,
                    [next_translation_attempt_at] = NULL
                FROM [integration].[inbound_message] AS [message]
                WHERE [message].[connector_code] = N'CX01'
                  AND [message].[message_type] = N'BomUpserted'
                  AND [message].[is_latest] = 1
                  AND [message].[translation_status] = N'completed'
                  AND EXISTS (
                      SELECT 1
                      FROM [integration].[exception_task] AS [task]
                      WHERE [task].[tenant_id] = [message].[tenant_id]
                        AND [task].[inbound_message_id] = [message].[inbound_message_id]
                        AND [task].[kind] = N'unsupported_inbound');
                IF @@ROWCOUNT = 0 BREAK;
            END

            -- The unsupported_inbound tasks on BOM messages were raised in error; nothing else closes them.
            WHILE 1 = 1
            BEGIN
                DELETE TOP (1000) [task]
                FROM [integration].[exception_task] AS [task]
                WHERE [task].[kind] = N'unsupported_inbound'
                  AND EXISTS (
                      SELECT 1
                      FROM [integration].[inbound_message] AS [message]
                      WHERE [message].[tenant_id] = [task].[tenant_id]
                        AND [message].[inbound_message_id] = [task].[inbound_message_id]
                        AND [message].[connector_code] = N'CX01'
                        AND [message].[message_type] = N'BomUpserted');
                IF @@ROWCOUNT = 0 BREAK;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The backfill must see every tenant's CX01 connection; the shared policy is off only inside this
            // migration transaction, as in P7T15BomProjection.
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = OFF)");
            migrationBuilder.Sql(BackfillSql);
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = ON)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the capability row is the one a probe writes, and P7T15BomProjection's Down removes
            // the bill-of-materials tables the re-translated trees fill.
        }
    }
}
