using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ledger;

/// <summary>Reads exact ledger idempotency identities without turning paired values into two independent sets.</summary>
public sealed class UnitEventIdempotencyReader(SerialisationDbContext db)
{
    // TIGHT COUPLING (partitioned idempotency seek)
    // WHAT: this query names and FORCESEEKs the unique index configured in SerialisationDbContext.ConfigureLedger.
    // WHY: SQL Server otherwise turns batched keys and business times into independent OPENJSON predicates and probes
    // all monthly partitions for their cross-product. One OPENJSON row per exact pair gives one aligned-index seek.
    // WHAT BREAKS IF SEPARATED: renaming or reordering that index breaks this query; removing the hint can restore the
    // 181-partition scan seen in the 1 M-unit concurrency run. Keep the matching model comment in step with this one.
    private const string ExactPairsSql = """
        SELECT ledger.[idempotency_key] AS [Key], ledger.[event_time] AS [At]
        FROM OPENJSON(@pairs) WITH (
            [Key] varchar(64) '$.key',
            [At] datetime2(3) '$.at'
        ) AS requested
        INNER LOOP JOIN [serialisation].[unit_event] AS ledger
            WITH (FORCESEEK, INDEX([ux_unit_event_tenant_id_idempotency_key_event_time]))
            ON ledger.[tenant_id] = @tenant
           AND ledger.[idempotency_key] = requested.[Key]
           AND ledger.[event_time] = requested.[At];
        """;

    public async Task<HashSet<(string Key, DateTime At)>> SeenAsync(
        TenantId tenantId,
        IReadOnlyCollection<(string Key, DateTime At)> pairs,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<(string, DateTime)>();
        foreach (var chunk in pairs.Distinct().Chunk(2000))
        {
            var json = JsonSerializer.Serialize(chunk.Select(pair => new PairInput(pair.Key, pair.At)), LedgerJson.Options);
            var rows = await db.Database.SqlQueryRaw<PairRow>(ExactPairsSql,
                new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenantId.Value },
                new SqlParameter("@pairs", SqlDbType.NVarChar, -1) { Value = json })
                .ToListAsync(cancellationToken);
            seen.UnionWith(rows.Select(row => (row.Key, row.At)));
        }

        return seen;
    }

    public async Task<bool> SeenAsync(TenantId tenantId, string key, DateTime at, CancellationToken cancellationToken) =>
        (await SeenAsync(tenantId, [(key, at)], cancellationToken)).Count != 0;

    private sealed record PairInput(string Key, DateTime At);

    private sealed class PairRow
    {
        public string Key { get; init; } = "";
        public DateTime At { get; init; }
    }
}
