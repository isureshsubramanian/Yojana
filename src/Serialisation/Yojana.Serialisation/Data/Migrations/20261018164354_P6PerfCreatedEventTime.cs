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
            migrationBuilder.AddColumn<DateTime>(
                name: "created_event_time",
                schema: "serialisation",
                table: "unit",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            // The partition time is carried beside the event id so certificate reads can seek without probing the
            // partitioned ledger by an unqualified id. RLS hides all tenants from a migration session, so this policy is
            // disabled only for this backfill. CreatedAt is retained solely as a defensive fallback for a legacy row
            // whose declared creator event is already inconsistent with the ledger. System versioning is paused so the
            // same immutable creation fact can be copied into every pre-existing history version.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit] SET (SYSTEM_VERSIONING = OFF);");
            migrationBuilder.Sql("""
                UPDATE [u] SET [created_event_time] = [e].[event_time]
                FROM [serialisation].[unit] AS [u]
                INNER JOIN [serialisation].[unit_event] AS [e]
                    ON [e].[tenant_id] = [u].[tenant_id] AND [e].[event_id] = [u].[created_event_id];

                UPDATE [u] SET [created_event_time] = [e].[event_time]
                FROM [serialisation].[unit] AS [u]
                INNER JOIN [serialisation].[unit_event_archive] AS [e]
                    ON [e].[tenant_id] = [u].[tenant_id] AND [e].[event_id] = [u].[created_event_id]
                WHERE [u].[created_event_time] IS NULL;

                UPDATE [serialisation].[unit]
                SET [created_event_time] = [created_at]
                WHERE [created_event_time] IS NULL;

                UPDATE [history] SET [created_event_time] = [current].[created_event_time]
                FROM [serialisation].[unit_history] AS [history]
                INNER JOIN [serialisation].[unit] AS [current]
                    ON [current].[tenant_id] = [history].[tenant_id] AND [current].[unit_id] = [history].[unit_id];
                """);
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit] ALTER COLUMN [created_event_time] datetime2(3) NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit_history] ALTER COLUMN [created_event_time] datetime2(3) NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE [serialisation].[unit] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [serialisation].[unit_history], DATA_CONSISTENCY_CHECK = ON));");
            migrationBuilder.AddTenantPolicy("serialisation", "unit");
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");
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
