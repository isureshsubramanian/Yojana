using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Search;

public sealed record SearchIndexHealingResult(int Units, int Documents);

/// <summary>
/// Cheap reconciliation behind the asynchronous projection. Unit and document entries carry the last ledger sequence
/// they observed; missing entries or sequence mismatches are rebuilt from source tables through the normal indexer.
/// </summary>
public sealed class SearchIndexHealer(
    SerialisationDbContext db,
    ITenantContext tenant,
    SearchIndexer indexer,
    ILogger<SearchIndexHealer> logger)
{
    private const string UnitCandidatesSql = """
        SELECT TOP (@batch) c.unit_id AS [Value]
        FROM serialisation.unit_current AS c
        LEFT JOIN serialisation.search_entry AS s
          ON s.tenant_id = c.tenant_id AND s.entity_type = N'unit' AND s.entity_id = c.unit_id
        WHERE c.tenant_id = @tenant
          AND (s.entity_id IS NULL
               OR TRY_CONVERT(bigint, JSON_VALUE(s.metadata, '$.last_event_seq')) IS NULL
               OR TRY_CONVERT(bigint, JSON_VALUE(s.metadata, '$.last_event_seq')) <> c.last_event_seq)
        ORDER BY c.unit_id;
        """;

    private const string DocumentCandidatesSql = """
        WITH latest AS
        (
            SELECT e.document_type, e.document_id, MAX(e.seq) AS last_event_seq
            FROM serialisation.unit_event AS e
            WHERE e.tenant_id = @tenant AND e.document_type IS NOT NULL AND e.document_id IS NOT NULL
            GROUP BY e.document_type, e.document_id
        )
        SELECT TOP (@batch) d.document_type AS DocumentType, d.document_id AS DocumentId
        FROM latest AS d
        LEFT JOIN serialisation.search_entry AS s
          ON s.tenant_id = @tenant AND s.entity_type = N'document' AND s.entity_id = d.document_id
        WHERE s.entity_id IS NULL
           OR TRY_CONVERT(bigint, JSON_VALUE(s.metadata, '$.last_event_seq')) IS NULL
           OR TRY_CONVERT(bigint, JSON_VALUE(s.metadata, '$.last_event_seq')) <> d.last_event_seq
        ORDER BY d.document_type, d.document_id;
        """;

    public async Task<SearchIndexHealingResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var tenantId = tenant.TenantId;
        var units = await db.Database.SqlQueryRaw<Guid>(UnitCandidatesSql,
                new SqlParameter("@tenant", tenantId.Value), new SqlParameter("@batch", batchSize))
            .ToArrayAsync(cancellationToken);
        await indexer.ReindexUnitsAsync(units, cancellationToken);

        var documents = await db.Database.SqlQueryRaw<SearchDocumentCandidate>(DocumentCandidatesSql,
                new SqlParameter("@tenant", tenantId.Value), new SqlParameter("@batch", batchSize))
            .ToArrayAsync(cancellationToken);
        foreach (var document in documents)
        {
            await indexer.ReindexDocumentAsync(document.DocumentType, document.DocumentId, cancellationToken);
        }

        logger.LogInformation("Search index healer for tenant {Tenant}: rebuilt {Units} unit entries and {Documents} document entries",
            tenantId, units.Length, documents.Length);
        return new SearchIndexHealingResult(units.Length, documents.Length);
    }

    public sealed class SearchDocumentCandidate
    {
        public required string DocumentType { get; set; }

        public Guid DocumentId { get; set; }
    }
}

/// <summary>Nightly tenant-local safety net for missed asynchronous search messages.</summary>
[DisallowConcurrentExecution]
public sealed class SearchIndexHealerJob(ITenantContextSetter setter, SearchIndexHealer healer) : TenantJob(setter)
{
    public const string JobName = "serialisation.search_index_healer";
    public const string Cron = "0 45 3 * * ?";
    public const int BatchSize = 5000;

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        healer.RunAsync(BatchSize, cancellationToken);
}
