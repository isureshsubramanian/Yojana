using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Search;

public sealed record SearchBackfillResult(long Units, int Batches, TimeSpan Elapsed);

/// <summary>Rebuilds a tenant's unit index in independently committed, restartable keyset batches.</summary>
public sealed class SearchBackfill(SerialisationDbContext db, ITenantContext tenant, SearchIndexer indexer, IClock clock)
{
    public const int DefaultBatchSize = 5000;

    /// <summary>
    /// Rebuilds every unit entry of the tenant. Each batch commits on its own, so a run stopped part-way is resumed by
    /// running it again (a batch replaces its units' rows whole). Document entries are left to their handlers.
    /// </summary>
    public async Task<SearchBackfillResult> BackfillUnitsAsync(CancellationToken cancellationToken, int batchSize = DefaultBatchSize)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var count = 0L;
        var batches = 0;
        Guid? after = null;
        while (true)
        {
            var ids = await db.Units.AsNoTracking().Where(u => u.TenantId == tenant.TenantId && (after == null || u.UnitId.CompareTo(after.Value) > 0))
                .OrderBy(u => u.UnitId).Select(u => u.UnitId).Take(batchSize).ToArrayAsync(cancellationToken);
            if (ids.Length == 0) break;
            after = ids[^1];
            await ReindexUnitsAsync(ids, cancellationToken);
            count += ids.Length;
            batches++;
        }
        return new SearchBackfillResult(count, batches, started.Elapsed);
    }

    /// <summary>Replaces an explicit unit set with the same staged, set-based path used by the full backfill.</summary>
    public async Task ReindexUnitsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        foreach (var ids in unitIds.Distinct().Order().Chunk(DefaultBatchSize))
        {
            var ownTransaction = db.Database.CurrentTransaction is null;
            await using var transactionScope = ownTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            // SearchIndexer holds the same tenant gate in Shared mode while writing. Exclusive here covers the
            // source read, delete and insert, so a live handler cannot write stale rows into this batch.
            await LockTenantAsync(db, tenant.TenantId, "Exclusive", cancellationToken);
            var drafts = await indexer.BuildUnitDraftsAsync(ids, ids.Length, cancellationToken);
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            await ExecuteAsync(connection, transaction, "DROP TABLE IF EXISTS #entry, #term; CREATE TABLE #entry (tenant_id uniqueidentifier, entity_type nvarchar(40), entity_id uniqueidentifier, entry_id uniqueidentifier, title nvarchar(200), title_mono bit, subtitle nvarchar(400), search_text nvarchar(max), type_weight int, updated_at datetime2, metadata nvarchar(max), href nvarchar(200)); CREATE TABLE #term (tenant_id uniqueidentifier, term nvarchar(200), entity_type nvarchar(40), entity_id uniqueidentifier, kind nvarchar(20), detail nvarchar(60));", cancellationToken);
            var entries = Table(("tenant_id", typeof(Guid)), ("entity_type", typeof(string)), ("entity_id", typeof(Guid)), ("entry_id", typeof(Guid)), ("title", typeof(string)), ("title_mono", typeof(bool)), ("subtitle", typeof(string)), ("search_text", typeof(string)), ("type_weight", typeof(int)), ("updated_at", typeof(DateTime)), ("metadata", typeof(string)), ("href", typeof(string)));
            var terms = Table(("tenant_id", typeof(Guid)), ("term", typeof(string)), ("entity_type", typeof(string)), ("entity_id", typeof(Guid)), ("kind", typeof(string)), ("detail", typeof(string)));
            var now = clock.UtcNow;
            foreach (var d in drafts)
            {
                entries.Rows.Add(tenant.TenantId.Value, d.EntityType, d.EntityId, TimeOrderedId.New(now), Clip(d.Title, 200), d.TitleMono, Db(d.Subtitle is null ? null : Clip(d.Subtitle, 400)), string.Join(' ', d.Terms.Select(t => t.Term).Distinct(StringComparer.OrdinalIgnoreCase)), d.TypeWeight, now, d.MetadataJson, d.Href);
                foreach (var term in d.Terms.Where(t => t.Term.Length > 0).DistinctBy(t => (t.Term, t.Kind)))
                    terms.Rows.Add(tenant.TenantId.Value, Clip(term.Term, 200), d.EntityType, d.EntityId, term.Kind, Db(term.Detail is null ? null : Clip(term.Detail, 60)));
            }
            await BulkAsync(connection, transaction, "#entry", entries, cancellationToken);
            await BulkAsync(connection, transaction, "#term", terms, cancellationToken);
            // Both predicates carry tenant and type; the joined ids only restrict the batch inside that key space.
            await ExecuteAsync(connection, transaction, "DELETE t FROM serialisation.search_term t JOIN #entry e ON e.tenant_id=t.tenant_id AND e.entity_type=t.entity_type AND e.entity_id=t.entity_id WHERE t.tenant_id=@tenant AND t.entity_type=N'unit'; DELETE s FROM serialisation.search_entry s JOIN #entry e ON e.tenant_id=s.tenant_id AND e.entity_type=s.entity_type AND e.entity_id=s.entity_id WHERE s.tenant_id=@tenant AND s.entity_type=N'unit'; INSERT serialisation.search_entry (tenant_id,entity_type,entity_id,entry_id,title,title_mono,subtitle,search_text,type_weight,updated_at,metadata,href) SELECT tenant_id,entity_type,entity_id,entry_id,title,title_mono,subtitle,search_text,type_weight,updated_at,metadata,href FROM #entry; INSERT serialisation.search_term (tenant_id,term,entity_type,entity_id,kind,detail) SELECT tenant_id,term,entity_type,entity_id,kind,detail FROM #term;", cancellationToken, tenant.TenantId.Value);
            await ExecuteAsync(connection, transaction, "DROP TABLE #entry, #term;", cancellationToken);
            if (transactionScope is not null) await transactionScope.CommitAsync(cancellationToken);
        }
    }

    internal static async Task LockTenantAsync(SerialisationDbContext db, TenantId tenantId, string mode, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource=@resource, @LockMode=@mode, @LockOwner=N'Transaction', @LockTimeout=20000; IF @r < 0 THROW 51000, 'search tenant busy', 1;";
        var resource = command.CreateParameter(); resource.ParameterName = "@resource"; resource.Value = "search:" + tenantId.ToString(); command.Parameters.Add(resource);
        var lockMode = command.CreateParameter(); lockMode.ParameterName = "@mode"; lockMode.Value = mode; command.Parameters.Add(lockMode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var (name, type) in columns) table.Columns.Add(name, type);
        return table;
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];

    private static async Task BulkAsync(SqlConnection connection, SqlTransaction transaction, string table, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = table, BatchSize = 5000, BulkCopyTimeout = 120 };
        foreach (DataColumn column in data.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken ct, Guid? tenantId = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        if (tenantId is { } id) command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = id });
        await command.ExecuteNonQueryAsync(ct);
    }
}
