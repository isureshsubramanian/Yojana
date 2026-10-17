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
        var raw = await query
            .OrderByDescending(r => db.RmaLines.Any(l => l.TenantId == tenantId && l.RmaId == r.RmaId
                && (l.ProvisionalUnitId != null || l.ReceiptOutcome != null && l.ReceiptOutcome != ReturnOutcome.Accepted)))
            .ThenBy(r => r.RequestedAt).ThenBy(r => r.RmaId).Skip(skip).Take(take)
            .Select(r => new
            {
                r.RmaId, r.RmaNumber, r.Stage, r.RequestDate, r.RequestedAt, r.ReturningPartyRef,
                UpdatedAt = r.ClosedAt ?? r.DispositionedAt ?? r.ReceivedAt ?? r.InTransitAt ?? r.AuthorisedAt ?? r.RequestedAt,
                r.ReceivedAt, r.RequestedBy, r.LastActorRef,
                LineCount = db.RmaLines.Count(l => l.TenantId == tenantId && l.RmaId == r.RmaId),
                ExceptionCount = db.RmaLines.Count(l => l.TenantId == tenantId && l.RmaId == r.RmaId
                    && (l.ProvisionalUnitId != null || (l.ReceiptOutcome != null && l.ReceiptOutcome != ReturnOutcome.Accepted))),
            }).ToListAsync(cancellationToken);
        var pageIds = raw.Select(r => r.RmaId).ToList();
        var pageLines = await db.RmaLines.AsNoTracking().Where(l => l.TenantId == tenantId && pageIds.Contains(l.RmaId))
            .OrderBy(l => l.Sequence).Select(l => new
            {
                l.RmaId, l.RequestedUnitId, l.ReceivedUnitId, l.ReceiptOutcome, l.ConditionGrade, l.CoverageSource,
            }).ToListAsync(cancellationToken);
        var primaryLines = pageLines.GroupBy(l => l.RmaId).ToDictionary(g => g.Key, g => g.First());
        var primaryUnitIds = primaryLines.Values.Select(l => l.ReceivedUnitId ?? l.RequestedUnitId).Distinct().ToList();
        var serials = await UnitSerials.DisplayAsync(db, tenantId, primaryUnitIds, cancellationToken);
        var itemIds = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && primaryUnitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var rows = raw.Select(r =>
        {
            primaryLines.TryGetValue(r.RmaId, out var line);
            var unitId = line?.ReceivedUnitId ?? line?.RequestedUnitId;
            return new RmaListRow(r.RmaId, r.RmaNumber, r.Stage, r.RequestDate, r.UpdatedAt,
                Math.Max(0, DateOnly.FromDateTime(now).DayNumber - r.RequestDate.DayNumber), r.ReturningPartyRef, r.LineCount, r.ExceptionCount,
                unitId, unitId is { } id ? serials.GetValueOrDefault(id) : null,
                unitId is { } itemUnit && itemIds.TryGetValue(itemUnit, out var itemId) ? itemId : null,
                line?.ReceiptOutcome, line?.ConditionGrade, line?.CoverageSource, r.ReceivedAt, r.RequestedBy, r.LastActorRef);
        }).ToList();
        var counts = await db.Rmas.AsNoTracking().Where(r => r.TenantId == tenantId).GroupBy(r => r.Stage)
            .Select(g => new { Stage = g.Key, Count = g.Count(), Oldest = g.Min(r => r.RequestDate) }).ToListAsync(cancellationToken);
        var exceptions = await (from line in db.RmaLines.AsNoTracking()
                                join rma in db.Rmas.AsNoTracking() on new { line.TenantId, line.RmaId } equals new { rma.TenantId, rma.RmaId }
                                where line.TenantId == tenantId && (line.ProvisionalUnitId != null || line.ReceiptOutcome != null && line.ReceiptOutcome != ReturnOutcome.Accepted)
                                group line by rma.Stage into grouped
                                select new { Stage = grouped.Key, Count = grouped.Select(line => line.RmaId).Distinct().Count() })
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(now);
        var summary = RmaStage.All.Select(stage =>
        {
            var count = counts.FirstOrDefault(x => x.Stage == stage);
            return new RmaStageCount(stage, count?.Count ?? 0, count is null ? null : Math.Max(0, today.DayNumber - count.Oldest.DayNumber),
                exceptions.FirstOrDefault(x => x.Stage == stage)?.Count ?? 0);
        }).ToList();
        return new(rows, summary, total, skip, take);
    }

    public async Task<RmaDetail?> DetailAsync(Guid rmaId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RmaId == rmaId, cancellationToken);
        if (rma is null) return null;
        var lines = await db.RmaLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.RmaId == rmaId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var receipts = await db.ReturnReceipts.AsNoTracking().Where(receipt => receipt.TenantId == tenantId && receipt.RmaId == rmaId)
            .ToDictionaryAsync(receipt => receipt.RmaLineId, cancellationToken);
        var unitIds = lines.SelectMany(line => new[] { (Guid?)line.RequestedUnitId, line.AuthorisedUnitId, line.ReceivedUnitId })
            .OfType<Guid>().Distinct().ToList();
        var serials = await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken);
        var items = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        return new RmaDetail(rma.RmaId, rma.RmaNumber, rma.Stage, rma.RequestDate, rma.ReturningPartyRef, rma.SettlementPartyRef,
            rma.ServiceEntityId, rma.ServiceCountry, rma.Currency, rma.RequestedAt, rma.AuthorisedAt, rma.InTransitAt,
            rma.ReceivedAt, rma.DispositionedAt, rma.ClosedAt, rma.RequestedBy, rma.LastActorRef, lines.Select(l =>
            {
                receipts.TryGetValue(l.RmaLineId, out var receipt);
                return new RmaDetailLine(l.RmaLineId, l.Sequence, l.RequestedUnitId, l.AuthorisedUnitId, l.OwnershipEpisodeId,
                    new DocumentRef(l.OriginalSaleDocumentType, l.OriginalSaleDocumentId, l.OriginalSaleDocumentLine), l.ParentUnitId,
                    l.PriceAllocation, l.ParentIncomplete, l.CoveragePct, l.ApprovedAmount, l.CoverageSource, l.EntitlementId,
                    l.DenialReason, l.ReceivedUnitId, l.ProvisionalUnitId, l.ReceiptOutcome, l.ConditionGrade, l.ValuationRule,
                    l.Disposition, l.ReplacementIntentId, l.EntitlementClosedReason, l.Description,
                    items.TryGetValue(l.RequestedUnitId, out var requestedItem) ? requestedItem : null, serials.GetValueOrDefault(l.RequestedUnitId),
                    l.AuthorisedUnitId is { } authorised ? serials.GetValueOrDefault(authorised) : null,
                    l.ReceivedUnitId is { } received && items.TryGetValue(received, out var receivedItem) ? receivedItem : null,
                    l.ReceivedUnitId is { } receivedId ? serials.GetValueOrDefault(receivedId) : null,
                    receipt?.BusinessTime, receipt?.PostingPeriod, receipt?.ReceivedBy, receipt?.ReceivingLocationId, receipt?.ExceptionNote);
            }).ToList());
    }
}
