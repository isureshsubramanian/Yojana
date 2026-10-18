using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6PerfCreatedEventTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The partition time is carried beside the event id so certificate reads can seek without probing the
            // partitioned ledger by an unqualified id. Existing databases may have 150 M+ ledger rows, so the fill is
            // restart-safe and commits 2,000 keyed units at a time. A read-only staging pass scans the ledger once;
            // the write batches never rescan ledger partitions and never hold a unit-table lock for that scan.
            // RLS hides all tenants from a migration session and system versioning would duplicate every current-row
            // update into history. Both are restored in the catch path as well as after success.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'serialisation.unit', N'created_event_time') IS NULL
                    ALTER TABLE [serialisation].[unit] ADD [created_event_time] datetime2(3) NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql($$"""
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                BEGIN TRY
                    ALTER SECURITY POLICY [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}] WITH (STATE = OFF);
                    IF EXISTS
                    (
                        SELECT 1 FROM sys.security_predicates
                        WHERE object_id = OBJECT_ID(N'[{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}]')
                          AND target_object_id = OBJECT_ID(N'[serialisation].[unit]')
                    )
                        EXEC(N'ALTER SECURITY POLICY [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}]
                            DROP FILTER PREDICATE ON [serialisation].[unit],
                            DROP BLOCK PREDICATE ON [serialisation].[unit] AFTER INSERT,
                            DROP BLOCK PREDICATE ON [serialisation].[unit] AFTER UPDATE;');
                    IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[serialisation].[unit]') AND temporal_type = 2)
                        ALTER TABLE [serialisation].[unit] SET (SYSTEM_VERSIONING = OFF);

                    CREATE TABLE #created_event_stage
                    (
                        [tenant_id] uniqueidentifier NOT NULL,
                        [unit_id] uniqueidentifier NOT NULL,
                        [created_event_time] datetime2(3) NOT NULL,
                        PRIMARY KEY ([tenant_id], [unit_id])
                    );
                    -- One read-only ledger pass materialises the exact creator event before any unit update. This keeps
                    -- the 150 M-row scan away from unit write locks; only the small keyed updates below lock unit rows.
                    INSERT INTO #created_event_stage ([tenant_id], [unit_id], [created_event_time])
                    SELECT [u].[tenant_id], [u].[unit_id], [e].[event_time]
                    FROM [serialisation].[unit] AS [u]
                    INNER HASH JOIN [serialisation].[unit_event] AS [e]
                        ON [e].[tenant_id] = [u].[tenant_id] AND [e].[event_id] = [u].[created_event_id]
                    WHERE [u].[created_event_time] IS NULL
                    OPTION (MAXDOP 1);

                    INSERT INTO #created_event_stage ([tenant_id], [unit_id], [created_event_time])
                    SELECT [u].[tenant_id], [u].[unit_id], [e].[event_time]
                    FROM [serialisation].[unit] AS [u]
                    INNER HASH JOIN [serialisation].[unit_event_archive] AS [e]
                        ON [e].[tenant_id] = [u].[tenant_id] AND [e].[event_id] = [u].[created_event_id]
                    WHERE [u].[created_event_time] IS NULL
                      AND NOT EXISTS
                          (SELECT 1 FROM #created_event_stage AS [staged]
                           WHERE [staged].[tenant_id] = [u].[tenant_id] AND [staged].[unit_id] = [u].[unit_id])
                    OPTION (MAXDOP 1);

                    INSERT INTO #created_event_stage ([tenant_id], [unit_id], [created_event_time])
                    SELECT [u].[tenant_id], [u].[unit_id], [u].[created_at]
                    FROM [serialisation].[unit] AS [u]
                    WHERE [u].[created_event_time] IS NULL
                      AND NOT EXISTS
                          (SELECT 1 FROM #created_event_stage AS [staged]
                           WHERE [staged].[tenant_id] = [u].[tenant_id] AND [staged].[unit_id] = [u].[unit_id]);

                    CREATE TABLE #created_event_batch
                    (
                        [tenant_id] uniqueidentifier NOT NULL,
                        [unit_id] uniqueidentifier NOT NULL,
                        [created_event_time] datetime2(3) NOT NULL,
                        PRIMARY KEY ([tenant_id], [unit_id])
                    );
                    DECLARE @rows int = 1;

                    WHILE @rows > 0
                    BEGIN
                        TRUNCATE TABLE #created_event_batch;
                        INSERT INTO #created_event_batch ([tenant_id], [unit_id], [created_event_time])
                        SELECT TOP (2000) [staged].[tenant_id], [staged].[unit_id], [staged].[created_event_time]
                        FROM #created_event_stage AS [staged]
                        ORDER BY [staged].[tenant_id], [staged].[unit_id];
                        SET @rows = @@ROWCOUNT;
                        IF @rows = 0 BREAK;

                        UPDATE [u] WITH (ROWLOCK)
                        SET [created_event_time] = [batch].[created_event_time]
                        FROM [serialisation].[unit] AS [u]
                        INNER JOIN #created_event_batch AS [batch]
                            ON [batch].[tenant_id] = [u].[tenant_id] AND [batch].[unit_id] = [u].[unit_id];

                        DELETE [staged]
                        FROM #created_event_stage AS [staged]
                        INNER JOIN #created_event_batch AS [batch]
                            ON [batch].[tenant_id] = [staged].[tenant_id] AND [batch].[unit_id] = [staged].[unit_id];
                    END;

                    -- Compile the history write only after SYSTEM_VERSIONING is off. SQL Server otherwise rejects the
                    -- whole batch while unit_history is still classified as a temporal history table.
                    EXEC(N'
                        DECLARE @history_rows int = 1;
                        WHILE @history_rows > 0
                        BEGIN
                            UPDATE TOP (2000) [history] WITH (ROWLOCK)
                            SET [created_event_time] = [current].[created_event_time]
                            FROM [serialisation].[unit_history] AS [history]
                            INNER LOOP JOIN [serialisation].[unit] AS [current]
                                ON [current].[tenant_id] = [history].[tenant_id]
                               AND [current].[unit_id] = [history].[unit_id]
                            WHERE [history].[created_event_time] IS NULL;
                            SET @history_rows = @@ROWCOUNT;
                        END;');

                    ALTER TABLE [serialisation].[unit] ALTER COLUMN [created_event_time] datetime2(3) NOT NULL;
                    EXEC(N'ALTER TABLE [serialisation].[unit_history] ALTER COLUMN [created_event_time] datetime2(3) NOT NULL;');
                    ALTER TABLE [serialisation].[unit] SET
                        (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [serialisation].[unit_history], DATA_CONSISTENCY_CHECK = ON));
                    EXEC(N'ALTER SECURITY POLICY [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}]
                        ADD FILTER PREDICATE [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PredicateFunction}}]([tenant_id]) ON [serialisation].[unit],
                        ADD BLOCK PREDICATE [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PredicateFunction}}]([tenant_id]) ON [serialisation].[unit] AFTER INSERT,
                        ADD BLOCK PREDICATE [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PredicateFunction}}]([tenant_id]) ON [serialisation].[unit] AFTER UPDATE;');
                    ALTER SECURITY POLICY [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}] WITH (STATE = ON);
                END TRY
                BEGIN CATCH
                    IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[serialisation].[unit]') AND temporal_type <> 2)
                       AND COL_LENGTH(N'serialisation.unit_history', N'created_event_time') IS NOT NULL
                        ALTER TABLE [serialisation].[unit] SET
                            (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [serialisation].[unit_history], DATA_CONSISTENCY_CHECK = ON));
                    IF NOT EXISTS
                    (
                        SELECT 1 FROM sys.security_predicates
                        WHERE object_id = OBJECT_ID(N'[{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}]')
                          AND target_object_id = OBJECT_ID(N'[serialisation].[unit]')
                    )
                        EXEC(N'ALTER SECURITY POLICY [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}]
                            ADD FILTER PREDICATE [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PredicateFunction}}]([tenant_id]) ON [serialisation].[unit],
                            ADD BLOCK PREDICATE [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PredicateFunction}}]([tenant_id]) ON [serialisation].[unit] AFTER INSERT,
                            ADD BLOCK PREDICATE [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PredicateFunction}}]([tenant_id]) ON [serialisation].[unit] AFTER UPDATE;');
                    ALTER SECURITY POLICY [{{TenantSecurityMigrations.SecuritySchema}}].[{{TenantSecurityMigrations.PolicyNameFor("serialisation")}}] WITH (STATE = ON);
                    THROW;
                END CATCH;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit] SET (SYSTEM_VERSIONING = OFF);");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit] DROP COLUMN [created_event_time];");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit_history] DROP COLUMN [created_event_time];");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [serialisation].[unit_history], DATA_CONSISTENCY_CHECK = ON));");
            migrationBuilder.AddTenantPolicy("serialisation", "unit");
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");
        }
    }
}
