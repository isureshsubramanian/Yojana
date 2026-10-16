using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yojana.Serialisation.Data.Migrations;

/// <summary>
/// T-SQL for the parts of the ledger EF Core migrations cannot express: the monthly partition function and
/// scheme, the two partitioned tables and their aligned indexes (ADR-0001 "Scale and storage"). Kept beside
/// the Initial migration so the DDL is reviewable as SQL; the model in <see cref="SerialisationDbContext"/>
/// maps the same columns and index names, so the snapshot and later migrations stay consistent.
/// </summary>
internal static class LedgerDdl
{
    private const string Schema = SerialisationDbContext.SchemaName;
    private const string Function = SerialisationDbContext.PartitionFunction;
    private const string Scheme = SerialisationDbContext.PartitionScheme;

    /// <summary>
    /// First boundary of the initial partition function. Rows with an earlier business time (migrated history,
    /// ADR-0001 "Migration and coexistence") land in the leftmost partition, which is cold by definition and is
    /// archived as a whole. From here the function is monthly; the partition lifecycle job (P1-T03) keeps twelve
    /// months of boundaries ahead of today and merges emptied ones after archival.
    /// </summary>
    public static readonly DateOnly FirstBoundary = new(2024, 1, 1);

    /// <summary>Last boundary the Initial migration creates (inclusive); the job extends from there.</summary>
    public static readonly DateOnly InitialLastBoundary = new(2027, 12, 1);

    public static IEnumerable<DateOnly> InitialBoundaries()
    {
        for (var month = FirstBoundary; month <= InitialLastBoundary; month = month.AddMonths(1))
        {
            yield return month;
        }
    }

    public static void CreatePartitioning(MigrationBuilder migrationBuilder)
    {
        var values = string.Join(", ", InitialBoundaries().Select(d => "'" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'"));
        migrationBuilder.Sql($"""
            IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{Function}')
                CREATE PARTITION FUNCTION [{Function}] (datetime2(3)) AS RANGE RIGHT FOR VALUES ({values});
            """);
        // Development and the first production layout: every partition on PRIMARY. One filegroup per year is an
        // operator choice recorded with the benchmark (ADR-0001 AI 1), applied by NEXT USED before new boundaries.
        migrationBuilder.Sql($"""
            IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{Scheme}')
                CREATE PARTITION SCHEME [{Scheme}] AS PARTITION [{Function}] ALL TO ([PRIMARY]);
            """);
    }

    public static void DropPartitioning(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{Scheme}') DROP PARTITION SCHEME [{Scheme}];");
        migrationBuilder.Sql($"IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{Function}') DROP PARTITION FUNCTION [{Function}];");
    }

    /// <summary>
    /// Creates a ledger table on the partition scheme. Clustered on (tenant_id, event_time, event_id): appends
    /// by business time, serves range scans, and puts the partition column in the key. Every nonclustered index
    /// is created ON the scheme with event_time in its key, which SQL Server requires for a unique index on a
    /// partitioned table and which SWITCH PARTITION requires of every index (verified P0-T08).
    /// </summary>
    public static void CreateLedgerTable(MigrationBuilder migrationBuilder, string table, string idempotencyIndex, string unitIndex)
    {
        migrationBuilder.Sql($"""
            CREATE TABLE [{Schema}].[{table}] (
                [tenant_id] uniqueidentifier NOT NULL,
                [event_time] datetime2(3) NOT NULL,
                [event_id] uniqueidentifier NOT NULL,
                [unit_id] uniqueidentifier NOT NULL,
                [seq] bigint NOT NULL,
                [event_type] nvarchar(60) NOT NULL,
                [biz_step] nvarchar(40) NULL,
                [disposition] nvarchar(40) NULL,
                [location_id] uniqueidentifier NULL,
                [legal_entity_id] uniqueidentifier NOT NULL,
                [actor_ref] nvarchar(200) NOT NULL,
                [document_type] nvarchar(40) NULL,
                [document_id] uniqueidentifier NULL,
                [document_line] int NULL,
                [recorded_time] datetime2(3) NOT NULL CONSTRAINT [df_{table}_recorded_time] DEFAULT (SYSUTCDATETIME()),
                [posting_period] char(7) NULL,
                [idempotency_key] varchar(64) NOT NULL,
                [reverses_event_id] uniqueidentifier NULL,
                [attributes] nvarchar(max) NULL,
                CONSTRAINT [pk_{table}] PRIMARY KEY CLUSTERED ([tenant_id], [event_time], [event_id]),
                CONSTRAINT [ck_{table}_attributes_json] CHECK ([attributes] IS NULL OR ISJSON([attributes]) = 1)
            ) ON [{Scheme}] ([event_time]);
            """);
        migrationBuilder.Sql($"""
            CREATE UNIQUE NONCLUSTERED INDEX [{idempotencyIndex}] ON [{Schema}].[{table}] ([tenant_id], [idempotency_key], [event_time])
                ON [{Scheme}] ([event_time]);
            """);
        migrationBuilder.Sql($"""
            CREATE NONCLUSTERED INDEX [{unitIndex}] ON [{Schema}].[{table}] ([tenant_id], [unit_id], [event_time] DESC)
                INCLUDE ([event_type], [biz_step], [disposition], [location_id], [seq], [recorded_time])
                ON [{Scheme}] ([event_time]);
            """);
    }

    /// <summary>
    /// Events by document (P1-T21 finding): the search index rebuilds a document's entry from the events that name
    /// it, the unit search filters by document and the completeness query reads a document's units; without this
    /// index each of those scanned the whole ledger (1.3 M page reads, 28 s at 150 M rows). Filtered to rows that
    /// carry a document, partition-aligned like every other index, and identical on the three ledger tables so
    /// SWITCH keeps working.
    /// </summary>
    public static void CreateDocumentIndex(MigrationBuilder migrationBuilder, string table, string indexName)
    {
        migrationBuilder.Sql($"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'[{Schema}].[{table}]'))
                CREATE NONCLUSTERED INDEX [{indexName}] ON [{Schema}].[{table}] ([tenant_id], [document_type], [document_id], [event_time])
                    INCLUDE ([unit_id], [document_line], [event_type])
                    WHERE [document_id] IS NOT NULL
                    ON [{Scheme}] ([event_time]);
            """);
    }

    public static void DropDocumentIndex(MigrationBuilder migrationBuilder, string table, string indexName)
    {
        migrationBuilder.Sql($"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'[{Schema}].[{table}]')) DROP INDEX [{indexName}] ON [{Schema}].[{table}];");
    }
}
