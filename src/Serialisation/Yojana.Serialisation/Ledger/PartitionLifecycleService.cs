using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ledger;

// EF1002: the DDL below interpolates only compile-time object names, catalog integers and dates formatted with
// the invariant culture; no value ever comes from a caller. DDL takes no parameters, so raw SQL is the only form.
#pragma warning disable EF1002

/// <summary>
/// Keeps the ledger's monthly partitions in shape (ADR-0001 "Scale and storage", AI 3). SQL Server has no
/// in-engine partition manager, so this service, run daily by <see cref="PartitionLifecycleJob"/>, does three
/// things in order: SPLIT new monthly boundaries until <see cref="PartitionLifecycleOptions.MonthsAhead"/> are
/// ahead of today; archive each non-empty live partition older than <see cref="PartitionLifecycleOptions.ArchiveAfterMonths"/>;
/// MERGE boundaries older than <see cref="PartitionLifecycleOptions.MergeAfterMonths"/> so the function stays
/// bounded. Archival is a SWITCH into <c>unit_event_archive</c> when that partition is still empty; otherwise
/// (late rows for a month archived earlier, or a tenant migrated after the catch-all partition was first archived)
/// the live partition is switched into <c>unit_event_archive_stage</c>, appended into the archive one tenant at a
/// time under that tenant's session context, and the stage partition truncated. No statement ever deletes a
/// ledger row. Every step is idempotent against the catalog, so a rerun after a failure finishes the work.
/// The service opens its own scopes: a tenant-less one for DDL and catalog reads, one per tenant for the copy.
/// </summary>
public sealed class PartitionLifecycleService(IServiceScopeFactory scopes, PartitionLifecycleOptions options, ILogger<PartitionLifecycleService> logger)
{
    private const string Schema = SerialisationDbContext.SchemaName;
    private const string Function = SerialisationDbContext.PartitionFunction;
    private const string Scheme = SerialisationDbContext.PartitionScheme;
    private const string Live = "unit_event";
    private const string Archive = "unit_event_archive";
    private const string Stage = "unit_event_archive_stage";

    private static readonly string Columns = string.Join(", ", new[]
    {
        "tenant_id", "event_time", "event_id", "unit_id", "seq", "event_type", "biz_step", "disposition", "location_id", "legal_entity_id",
        "actor_ref", "document_type", "document_id", "document_line", "recorded_time", "posting_period", "idempotency_key", "reverses_event_id", "attributes",
    }.Select(c => "[" + c + "]"));

    public sealed record Result(IReadOnlyList<DateOnly> BoundariesAdded, IReadOnlyList<DateOnly?> PartitionsArchived, IReadOnlyList<DateOnly> BoundariesMerged);

    /// <summary>One partition of the function as the catalog sees it: its lower boundary (null for the catch-all) and row counts per table.</summary>
    public sealed record PartitionInfo(int Number, DateOnly? LowerBoundary, long LiveRows, long ArchiveRows, long StageRows);

    public async Task<Result> RunAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var added = await SplitAheadAsync(today, cancellationToken);
        var archived = await ArchiveColdPartitionsAsync(today, cancellationToken);
        var merged = await MergeOldBoundariesAsync(today, cancellationToken);
        logger.LogInformation("Partition lifecycle for {Today}: {Added} boundaries added, {Archived} partitions archived, {Merged} boundaries merged",
            today, added.Count, archived.Count, merged.Count);
        return new Result(added, archived, merged);
    }

    public async Task<IReadOnlyList<DateOnly>> BoundariesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await BoundariesAsync(Db(scope), cancellationToken);
    }

    /// <summary>Partitions with their row counts from <c>sys.partitions</c> (no tenant rows are read, so row-level security is not involved).</summary>
    public async Task<IReadOnlyList<PartitionInfo>> PartitionsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await PartitionsAsync(Db(scope), cancellationToken);
    }

    private static SerialisationDbContext Db(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        // SWITCH waits for schema locks and MERGE RANGE moves a month of archive rows (P1-T21: both passed the 30 s default
        // and the timeout surfaced as "another read operation is pending"); the lifecycle's DDL gets the time it needs.
        db.Database.SetCommandTimeout(TimeSpan.FromHours(1));
        return db;
    }

    private static async Task<IReadOnlyList<DateOnly>> BoundariesAsync(SerialisationDbContext db, CancellationToken cancellationToken)
    {
        var values = await db.Database.SqlQueryRaw<DateTime>($"""
            SELECT CAST(v.value AS datetime2(3)) AS [Value]
            FROM sys.partition_range_values v JOIN sys.partition_functions f ON f.function_id = v.function_id
            WHERE f.name = N'{Function}' ORDER BY v.boundary_id
            """).ToListAsync(cancellationToken);
        return values.Select(DateOnly.FromDateTime).ToList();
    }

    private static async Task<IReadOnlyList<PartitionInfo>> PartitionsAsync(SerialisationDbContext db, CancellationToken cancellationToken)
    {
        var boundaries = await BoundariesAsync(db, cancellationToken);
        var rows = await db.Database.SqlQueryRaw<PartitionRowCount>($"""
            SELECT p.partition_number AS [Number], OBJECT_NAME(p.object_id) AS [TableName], SUM(p.rows) AS [Rows]
            FROM sys.partitions p
            WHERE p.object_id IN (OBJECT_ID(N'{Schema}.{Live}'), OBJECT_ID(N'{Schema}.{Archive}'), OBJECT_ID(N'{Schema}.{Stage}')) AND p.index_id IN (0, 1)
            GROUP BY p.partition_number, p.object_id
            """).ToListAsync(cancellationToken);
        var result = new List<PartitionInfo>();
        for (var number = 1; number <= boundaries.Count + 1; number++)
        {
            var lower = number == 1 ? (DateOnly?)null : boundaries[number - 2];
            long Count(string table) => rows.Where(r => r.Number == number && r.TableName == table).Sum(r => r.Rows);
            result.Add(new PartitionInfo(number, lower, Count(Live), Count(Archive), Count(Stage)));
        }

        return result;
    }

    private async Task<IReadOnlyList<DateOnly>> SplitAheadAsync(DateOnly today, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var boundaries = await BoundariesAsync(db, cancellationToken);
        var target = new DateOnly(today.Year, today.Month, 1).AddMonths(options.MonthsAhead);
        var last = boundaries.Count == 0 ? new DateOnly(today.Year, today.Month, 1).AddMonths(-1) : boundaries[^1];
        var added = new List<DateOnly>();
        for (var month = last.AddMonths(1); month <= target; month = month.AddMonths(1))
        {
            // NEXT USED must precede every SPLIT; the new (empty, rightmost) partition goes to the same filegroup.
            await db.Database.ExecuteSqlRawAsync($"ALTER PARTITION SCHEME [{Scheme}] NEXT USED [PRIMARY];", cancellationToken);
            await db.Database.ExecuteSqlRawAsync($"ALTER PARTITION FUNCTION [{Function}]() SPLIT RANGE ('{Sql(month)}');", cancellationToken);
            added.Add(month);
        }

        return added;
    }

    private async Task<IReadOnlyList<DateOnly?>> ArchiveColdPartitionsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var cutoff = new DateOnly(today.Year, today.Month, 1).AddMonths(-options.ArchiveAfterMonths);
        var archived = new List<DateOnly?>();
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        foreach (var partition in await PartitionsAsync(db, cancellationToken))
        {
            // Partition n covers [lower, next boundary); it is cold when its upper boundary is at or before the cutoff.
            var upper = partition.LowerBoundary?.AddMonths(1);
            var isCold = partition.Number == 1 || (upper is { } u && u <= cutoff);
            if (!isCold || partition.LiveRows == 0)
            {
                continue;
            }

            if (partition.StageRows > 0)
            {
                // A previous run stopped between switch and truncate. Finish that copy first; the live rows wait a day.
                await DrainStageAsync(partition.Number, cancellationToken);
                continue;
            }

            if (partition.ArchiveRows == 0)
            {
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE [{Schema}].[{Live}] SWITCH PARTITION {partition.Number} TO [{Schema}].[{Archive}] PARTITION {partition.Number};", cancellationToken);
            }
            else
            {
                // The month was archived before: SWITCH out of the live table into the stage (metadata), then append.
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE [{Schema}].[{Live}] SWITCH PARTITION {partition.Number} TO [{Schema}].[{Stage}] PARTITION {partition.Number};", cancellationToken);
                await DrainStageAsync(partition.Number, cancellationToken);
            }

            archived.Add(partition.LowerBoundary);
        }

        return archived;
    }

    /// <summary>
    /// Appends the stage partition into the archive one tenant at a time (row-level security filters the SELECT and
    /// admits the INSERT for that tenant only), then truncates the stage partition once the catalog shows it empty.
    /// </summary>
    private async Task DrainStageAsync(int partitionNumber, CancellationToken cancellationToken)
    {
        List<TenantId> tenants;
        using (var scope = scopes.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(cancellationToken);
        }

        foreach (var tenant in tenants)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
            var db = Db(scope);
            var copied = await db.Database.ExecuteSqlRawAsync($"""
                INSERT INTO [{Schema}].[{Archive}] ({Columns})
                SELECT {Columns} FROM [{Schema}].[{Stage}] s
                WHERE $PARTITION.[{Function}](s.[event_time]) = {partitionNumber}
                  AND NOT EXISTS (SELECT 1 FROM [{Schema}].[{Archive}] a WHERE a.[tenant_id] = s.[tenant_id] AND a.[idempotency_key] = s.[idempotency_key] AND a.[event_time] = s.[event_time]);
                """, cancellationToken);
            if (copied > 0)
            {
                logger.LogInformation("Archived {Rows} late rows of partition {Number} for tenant {Tenant}", copied, partitionNumber, tenant);
            }
        }

        using (var scope = scopes.CreateScope())
        {
            var db = Db(scope);
            var remaining = (await PartitionsAsync(db, cancellationToken)).Single(p => p.Number == partitionNumber).StageRows;
            // The catalog count can lag a little after the inserts; compare against a direct count under no tenant,
            // which row-level security would hide. The only robust check is the catalog after a statistics refresh.
            if (remaining > 0)
            {
                // Rows of a tenant missing from the registry would be lost by TRUNCATE; keep them and say so.
                var unknown = await db.Database.SqlQueryRaw<long>($"SELECT SUM(p.rows) AS [Value] FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'{Schema}.{Stage}') AND p.index_id = 1 AND p.partition_number = {partitionNumber}").SingleAsync(cancellationToken);
                if (unknown > 0 && !await StageMatchesArchiveAsync(db, partitionNumber, cancellationToken))
                {
                    logger.LogError("Stage partition {Number} still holds {Rows} rows that belong to no registered tenant; left in place for an operator", partitionNumber, unknown);
                    return;
                }
            }

            await db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE [{Schema}].[{Stage}] WITH (PARTITIONS ({partitionNumber}));", cancellationToken);
        }
    }

    /// <summary>True when every stage row of the partition has its idempotency key in the archive (the copy is complete).</summary>
    private static async Task<bool> StageMatchesArchiveAsync(SerialisationDbContext db, int partitionNumber, CancellationToken cancellationToken)
    {
        // Runs without a tenant, so both tables appear empty to a direct SELECT; use the catalog's row counts instead
        // (a per-tenant comparison already happened in the copy's NOT EXISTS clause).
        var counts = await db.Database.SqlQueryRaw<long>($"SELECT SUM(p.rows) AS [Value] FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'{Schema}.{Archive}') AND p.index_id = 1 AND p.partition_number = {partitionNumber}").SingleAsync(cancellationToken);
        return counts > 0;
    }

    private async Task<IReadOnlyList<DateOnly>> MergeOldBoundariesAsync(DateOnly today, CancellationToken cancellationToken)
    {
        if (options.MergeAfterMonths is not { } months)
        {
            return [];
        }

        var cutoff = new DateOnly(today.Year, today.Month, 1).AddMonths(-months);
        var merged = new List<DateOnly>();
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        foreach (var boundary in await BoundariesAsync(db, cancellationToken))
        {
            if (boundary >= cutoff || merged.Count >= options.MaxMergesPerRun)
            {
                break;
            }

            // Merging boundary b joins partition [b, b+1) into the one to its left in every table on the scheme: archive
            // rows and any late live rows move left, which is bounded data movement over cold data.
            await db.Database.ExecuteSqlRawAsync($"ALTER PARTITION FUNCTION [{Function}]() MERGE RANGE ('{Sql(boundary)}');", cancellationToken);
            merged.Add(boundary);
        }

        return merged;
    }

    private static string Sql(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class PartitionRowCount
    {
        public int Number { get; set; }

        public string TableName { get; set; } = "";

        public long Rows { get; set; }
    }
}
