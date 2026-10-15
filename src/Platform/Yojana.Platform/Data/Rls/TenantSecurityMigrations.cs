using Microsoft.EntityFrameworkCore.Migrations;

namespace Yojana.Platform.Data.Rls;

/// <summary>
/// Row-level security by migration convention (ADR-0001 "Scale and storage", Tenancy). The platform
/// migration creates the <c>sec</c> schema and the schema-bound predicate function once. Every module
/// migration that creates a tenant table calls <see cref="AddTenantPolicy"/>, which adds the FILTER and
/// BLOCK predicates for that table to the module's own policy <c>sec.tenant_policy_{schema}</c>. One
/// policy per schema keeps module migrations independent of each other: the only shared object is the
/// read-only predicate function, which must exist first (the host applies platform migrations before
/// module migrations; a module migration that runs too early fails loudly on the missing function).
/// </summary>
public static class TenantSecurityMigrations
{
    public const string SecuritySchema = "sec";
    public const string PredicateFunction = "fn_tenant_predicate";

    public static void EnsureTenantSecurityObjects(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"IF SCHEMA_ID(N'{SecuritySchema}') IS NULL EXEC(N'CREATE SCHEMA [{SecuritySchema}]');");
        migrationBuilder.Sql($"""
            IF OBJECT_ID(N'[{SecuritySchema}].[{PredicateFunction}]', N'IF') IS NULL
            EXEC(N'CREATE FUNCTION [{SecuritySchema}].[{PredicateFunction}] (@tenant_id uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS ok WHERE @tenant_id = CAST(SESSION_CONTEXT(N''tenant_id'') AS uniqueidentifier);');
            """);
    }

    public static string PolicyNameFor(string schema) => "tenant_policy_" + schema;

    /// <summary>Adds the FILTER plus BLOCK (after insert, after update) predicate pair for one tenant table.</summary>
    public static void AddTenantPolicy(this MigrationBuilder migrationBuilder, string schema, string table, string column = "tenant_id")
    {
        var policy = PolicyNameFor(schema);
        var predicate = $"[{SecuritySchema}].[{PredicateFunction}]([{column}])";
        var target = $"[{schema}].[{table}]";
        migrationBuilder.Sql($"""
            IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'{policy}' AND schema_id = SCHEMA_ID(N'{SecuritySchema}'))
                EXEC(N'CREATE SECURITY POLICY [{SecuritySchema}].[{policy}]
                    ADD FILTER PREDICATE {predicate} ON {target},
                    ADD BLOCK PREDICATE {predicate} ON {target} AFTER INSERT,
                    ADD BLOCK PREDICATE {predicate} ON {target} AFTER UPDATE
                    WITH (STATE = ON, SCHEMABINDING = ON);');
            ELSE
                EXEC(N'ALTER SECURITY POLICY [{SecuritySchema}].[{policy}]
                    ADD FILTER PREDICATE {predicate} ON {target},
                    ADD BLOCK PREDICATE {predicate} ON {target} AFTER INSERT,
                    ADD BLOCK PREDICATE {predicate} ON {target} AFTER UPDATE;');
            """);
    }

    public static void RemoveTenantPolicy(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        var policy = PolicyNameFor(schema);
        var target = $"[{schema}].[{table}]";
        migrationBuilder.Sql($"""
            IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'{policy}' AND schema_id = SCHEMA_ID(N'{SecuritySchema}'))
                EXEC(N'ALTER SECURITY POLICY [{SecuritySchema}].[{policy}]
                    DROP FILTER PREDICATE ON {target},
                    DROP BLOCK PREDICATE ON {target} AFTER INSERT,
                    DROP BLOCK PREDICATE ON {target} AFTER UPDATE;');
            """);
    }

    /// <summary>Read-committed snapshot isolation keeps readers off writers' locks (Azure SQL default; set explicitly on SQL Server).</summary>
    public static void EnableReadCommittedSnapshot(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0)
                EXEC(N'ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;');
            """, suppressTransaction: true);
    }
}
