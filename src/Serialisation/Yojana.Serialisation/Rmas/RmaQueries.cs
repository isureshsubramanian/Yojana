using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Rmas;

public sealed class RmaQueries(SerialisationDbContext db, IIdentifierPolicySource identifierPolicies, ITenantContext tenant)
{
    public async Task<RmaListResult> ListAsync(RmaListFilter filter, DateTime now, int skip, int take, CancellationToken cancellationToken)
    {
        if (filter.Stage is not null && !RmaStage.All.Contains(filter.Stage)) throw new ArgumentException($"Unknown RMA stage '{filter.Stage}'.", nameof(filter));
        if (filter.MinimumAgeDays is < 0 || filter.MaximumAgeDays is < 0 || filter.MinimumAgeDays > filter.MaximumAgeDays)
            throw new ArgumentException("RMA age bounds must be non-negative and minimum cannot exceed maximum.", nameof(filter));
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var tenantId = tenant.TenantId;
        var query = db.Rmas.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (filter.Stage is not null) query = query.Where(r => r.Stage == filter.Stage);
        if (filter.MinimumAgeDays is { } minimum) { var before = now.AddDays(-minimum); query = query.Where(r => r.RequestedAt <= before); }
        if (filter.MaximumAgeDays is { } maximum) { var after = now.AddDays(-(maximum + 1)); query = query.Where(r => r.RequestedAt > after); }
        if (!string.IsNullOrWhiteSpace(filter.Find))
        {
            var find = filter.Find.Trim();
            var normalised = IdentifierNormaliser.Normalise(find, identifierPolicies.For(IdentifierType.InternalSerial)).Value;
            var rmaNumber = find.ToUpperInvariant();
            var unitIds = await db.Identifiers.AsNoTracking()
                .Where(i => i.TenantId == tenantId && (i.NormalisedValue.Contains(normalised) || i.RawValue.Contains(find)))
                .Select(i => i.UnitId).Distinct().ToListAsync(cancellationToken);
            query = query.Where(r => (r.RmaNumber != null && r.RmaNumber.Contains(rmaNumber))
                || db.RmaLines.Any(l => l.TenantId == tenantId && l.RmaId == r.RmaId && unitIds.Contains(l.RequestedUnitId)));
        }
        var total = await query.CountAsync(cancellationToken);
        var raw = await query.OrderByDescending(r => r.RequestedAt).Skip(skip).Take(take)
            .Select(r => new
            {
                r.RmaId, r.RmaNumber, r.Stage, r.RequestDate, r.RequestedAt, r.ReturningPartyRef,
                UpdatedAt = r.ClosedAt ?? r.DispositionedAt ?? r.ReceivedAt ?? r.InTransitAt ?? r.AuthorisedAt ?? r.RequestedAt,
                LineCount = db.RmaLines.Count(l => l.TenantId == tenantId && l.RmaId == r.RmaId),
                ExceptionCount = db.RmaLines.Count(l => l.TenantId == tenantId && l.RmaId == r.RmaId
                    && (l.ProvisionalUnitId != null || (l.ReceiptOutcome != null && l.ReceiptOutcome != ReturnOutcome.Accepted))),
            }).ToListAsync(cancellationToken);
        var rows = raw.Select(r => new RmaListRow(r.RmaId, r.RmaNumber, r.Stage, r.RequestDate, r.UpdatedAt,
            Math.Max(0, (DateOnly.FromDateTime(now).DayNumber - r.RequestDate.DayNumber)), r.ReturningPartyRef, r.LineCount, r.ExceptionCount)).ToList();
        var counts = await db.Rmas.AsNoTracking().Where(r => r.TenantId == tenantId).GroupBy(r => r.Stage).Select(g => new { Stage = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var summary = RmaStage.All.Select(stage => new RmaStageCount(stage, counts.FirstOrDefault(x => x.Stage == stage)?.Count ?? 0)).ToList();
        return new(rows, summary, total, skip, take);
    }

    public async Task<RmaDetail?> DetailAsync(Guid rmaId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RmaId == rmaId, cancellationToken);
        if (rma is null) return null;
        var lines = await db.RmaLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.RmaId == rmaId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        return new RmaDetail(rma.RmaId, rma.RmaNumber, rma.Stage, rma.RequestDate, rma.ReturningPartyRef, rma.SettlementPartyRef,
            rma.ServiceEntityId, rma.ServiceCountry, rma.Currency, rma.RequestedAt, rma.AuthorisedAt, rma.InTransitAt,
            rma.ReceivedAt, rma.DispositionedAt, rma.ClosedAt, lines.Select(l => new RmaDetailLine(
                l.RmaLineId, l.Sequence, l.RequestedUnitId, l.AuthorisedUnitId, l.OwnershipEpisodeId,
                new DocumentRef(l.OriginalSaleDocumentType, l.OriginalSaleDocumentId, l.OriginalSaleDocumentLine), l.ParentUnitId,
                l.PriceAllocation, l.ParentIncomplete, l.CoveragePct, l.ApprovedAmount, l.CoverageSource, l.EntitlementId,
                l.DenialReason, l.ReceivedUnitId, l.ProvisionalUnitId, l.ReceiptOutcome, l.ConditionGrade, l.ValuationRule,
                l.Disposition, l.ReplacementIntentId, l.EntitlementClosedReason)).ToList());
    }
}
