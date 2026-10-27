using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <summary>Backfills the declaration stored before ADR-0025 and requeues issue messages skipped as unsupported.</summary>
    public partial class Adr25ComponentsIssuedCapability : Migration
    {
        public const string BackfillSql = """
            WHILE 1 = 1
            BEGIN
                INSERT INTO [integration].[connection_capability]
                    ([tenant_id], [connection_id], [canonical_message], [support_level])
                SELECT TOP (1000) [connection].[tenant_id], [connection].[connection_id],
                    N'ComponentsIssued', N'native'
                FROM [integration].[connection] AS [connection]
                WHERE [connection].[connector_code] = N'CX01'
                  AND NOT EXISTS (
                      SELECT 1 FROM [integration].[connection_capability] AS [capability]
                      WHERE [capability].[tenant_id] = [connection].[tenant_id]
                        AND [capability].[connection_id] = [connection].[connection_id]
                        AND [capability].[canonical_message] = N'ComponentsIssued');
                IF @@ROWCOUNT = 0 BREAK;
            END

            WHILE 1 = 1
            BEGIN
                UPDATE TOP (1000) [message]
                SET [translation_status] = N'pending', [translated_at] = NULL,
                    [translation_started_at] = NULL, [next_translation_attempt_at] = NULL
                FROM [integration].[inbound_message] AS [message]
                WHERE [message].[connector_code] = N'CX01'
                  AND [message].[message_type] = N'ComponentsIssued'
                  AND [message].[is_latest] = 1
                  AND [message].[translation_status] = N'completed'
                  AND EXISTS (
                      SELECT 1 FROM [integration].[exception_task] AS [task]
                      WHERE [task].[tenant_id] = [message].[tenant_id]
                        AND [task].[inbound_message_id] = [message].[inbound_message_id]
                        AND [task].[kind] = N'unsupported_inbound');
                IF @@ROWCOUNT = 0 BREAK;
            END

            WHILE 1 = 1
            BEGIN
                DELETE TOP (1000) [task]
                FROM [integration].[exception_task] AS [task]
                WHERE [task].[kind] = N'unsupported_inbound'
                  AND EXISTS (
                      SELECT 1 FROM [integration].[inbound_message] AS [message]
                      WHERE [message].[tenant_id] = [task].[tenant_id]
                        AND [message].[inbound_message_id] = [task].[inbound_message_id]
                        AND [message].[connector_code] = N'CX01'
                        AND [message].[message_type] = N'ComponentsIssued');
                IF @@ROWCOUNT = 0 BREAK;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = OFF)");
            migrationBuilder.Sql(BackfillSql);
            migrationBuilder.Sql("ALTER SECURITY POLICY [sec].[tenant_policy_integration] WITH (STATE = ON)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
