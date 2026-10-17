using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Rmas;

public sealed class CoverageDecisionQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<CoverageDecisionHistory> ForUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = await db.CoverageDecisions.AsNoTracking().Where(d => d.TenantId == tenantId && d.UnitId == unitId)
            .OrderByDescending(d => d.DecidedAt).ThenByDescending(d => d.CoverageDecisionId).ToListAsync(cancellationToken);
        return new(rows.Select(d => new CoverageDecisionRow(d.CoverageDecisionId, d.UnitId, d.Reason, d.WorkType,
            d.WorkId, d.WorkLineId, Snapshot(d.BeforeDecision), Snapshot(d.AfterDecision), d.SourceEventId,
            d.DecidedAt, d.ActorRef)).ToList());
    }

    private static CoverageDecisionSnapshot Snapshot(string json) =>
        JsonSerializer.Deserialize<CoverageDecisionSnapshot>(json, LedgerJson.Options)!;
}
