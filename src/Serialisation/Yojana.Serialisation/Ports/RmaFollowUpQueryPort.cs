using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Storage adapter for RMA follow-up completion. The RMA lifecycle sees only this narrow contract; replacement,
/// refurbishment, entitlement and ownership storage can change without changing the closing rules or handlers.
/// </summary>
public sealed class RmaFollowUpQueryPort(SerialisationDbContext db, ITenantContext tenant) : IRmaFollowUpQueryPort
{
    public async Task<IReadOnlyList<RmaOpenFollowUp>> OpenAsync(Guid rmaId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var lines = await db.RmaLines.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.RmaId == rmaId)
            .OrderBy(row => row.Sequence)
            .Select(row => new
            {
                row.RmaLineId,
                row.Sequence,
                row.Disposition,
                row.RequestedUnitId,
                row.AuthorisedUnitId,
                row.ReceivedUnitId,
                row.EntitlementId,
                row.EntitlementClosedReason,
            })
            .ToListAsync(cancellationToken);

        var lineIds = lines.Select(row => row.RmaLineId).ToList();
        var unitIds = lines.Select(row => row.ReceivedUnitId ?? row.AuthorisedUnitId ?? row.RequestedUnitId).Distinct().ToList();
        var entitlementIds = lines.Select(row => row.EntitlementId).OfType<Guid>().Distinct().ToList();

        var postedReplacementUnits = await db.ReplacementLinks.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.SourceKind == ReplacementSourceKind.Rma && row.SourceId == rmaId
                && row.ShippedEventId != null
                && (row.State == ReplacementLinkState.AwaitingReturn || row.State == ReplacementLinkState.ClosedReturned
                    || row.State == ReplacementLinkState.ClosedBilled))
            .Select(row => row.OriginalUnitId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var openedRefurbishmentLines = await db.RefurbishmentWorkOrders.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.SourceRmaLineId != null && lineIds.Contains(row.SourceRmaLineId.Value))
            .Select(row => row.SourceRmaLineId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var entitlementStates = await db.Entitlements.AsNoTracking()
            .Where(row => row.TenantId == tenantId && entitlementIds.Contains(row.EntitlementId))
            .Select(row => new { row.EntitlementId, row.Status, row.ClosedReason })
            .ToListAsync(cancellationToken);
        var vendorOwnedUnits = await db.Episodes.AsNoTracking()
            .Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId) && row.Kind == EpisodeKind.Ownership
                && row.ValidTo == null && row.PartyType == PartyType.Vendor)
            .Select(row => row.UnitId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var replacementSet = postedReplacementUnits.ToHashSet();
        var refurbishmentSet = openedRefurbishmentLines.ToHashSet();
        var vendorSet = vendorOwnedUnits.ToHashSet();
        var entitlementMap = entitlementStates.ToDictionary(row => row.EntitlementId);
        var open = new List<RmaOpenFollowUp>();

        foreach (var line in lines)
        {
            if (line.Disposition is null)
            {
                open.Add(new(line.RmaLineId, line.Sequence, "pending", "disposition_missing", "Choose and record a disposition for this line."));
                continue;
            }

            var unitId = line.ReceivedUnitId ?? line.AuthorisedUnitId ?? line.RequestedUnitId;
            var entitlementClosedAsRefunded = line.EntitlementId is { } entitlementId
                && entitlementMap.TryGetValue(entitlementId, out var entitlement)
                && entitlement.Status == EntitlementStatus.Closed
                && entitlement.ClosedReason == EntitlementClosedReason.Refunded
                && line.EntitlementClosedReason == EntitlementClosedReason.Refunded;
            if (line.Disposition == RmaDisposition.Replace && !replacementSet.Contains(unitId))
                open.Add(new(line.RmaLineId, line.Sequence, line.Disposition, "replacement_not_shipped", "Ship the warranty replacement document."));
            else if (line.Disposition == RmaDisposition.Refurbish && !refurbishmentSet.Contains(line.RmaLineId))
                open.Add(new(line.RmaLineId, line.Sequence, line.Disposition, "refurbishment_not_open", "Open the refurbishment work order."));
            else if (line.Disposition is RmaDisposition.Credit or RmaDisposition.Refund && !entitlementClosedAsRefunded)
                open.Add(new(line.RmaLineId, line.Sequence, line.Disposition, "entitlement_not_closed", "Close the entitlement as refunded."));
            else if (line.Disposition == RmaDisposition.ReturnToVendor && !vendorSet.Contains(unitId))
                open.Add(new(line.RmaLineId, line.Sequence, line.Disposition, "vendor_ownership_not_recorded", "Record the vendor ownership episode."));
        }

        return open;
    }
}
