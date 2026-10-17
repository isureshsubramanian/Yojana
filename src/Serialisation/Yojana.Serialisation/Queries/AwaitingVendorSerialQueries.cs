using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Queries;

/// <summary>
/// Read model for the production-order remainder left by AssignIdentifiers. Like that command, it consumes the narrow
/// CommissionUnits creating-fact contract: the production-order document identifies membership and ledger seq orders it.
/// </summary>
public sealed class AwaitingVendorSerialQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<AwaitingVendorSerialReport> ByProductionOrderAsync(DocumentRef productionOrder, CancellationToken cancellationToken)
    {
        if (productionOrder.Type != "production_order") throw new CommandRefusedException("production_order_required", "The awaiting-vendor-serial report requires a production_order document reference.");
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && e.DocumentType == productionOrder.Type && e.DocumentId == productionOrder.Id
                && (productionOrder.Line == null || e.DocumentLine == productionOrder.Line) && (e.EventType == UnitEventType.Commissioned || e.EventType == UnitEventType.CreatedPendingIdentifier))
            .Select(e => new { e.UnitId, e.Seq, e.EventTime }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && e.DocumentType == productionOrder.Type && e.DocumentId == productionOrder.Id
                && (productionOrder.Line == null || e.DocumentLine == productionOrder.Line) && (e.EventType == UnitEventType.Commissioned || e.EventType == UnitEventType.CreatedPendingIdentifier))
            .Select(e => new { e.UnitId, e.Seq, e.EventTime }).ToListAsync(cancellationToken);
        var created = live.Concat(archived).GroupBy(e => e.UnitId).Select(g => g.OrderBy(e => e.Seq).First()).ToList();
        var unitIds = created.Select(e => e.UnitId).ToList();
        if (unitIds.Count == 0) return new(productionOrder, 0, []);

        var units = await db.Units.AsNoTracking().Where(u => u.TenantId == tenant.TenantId && unitIds.Contains(u.UnitId)).ToListAsync(cancellationToken);
        var flagged = units.Where(u => (JsonSerializer.Deserialize<List<string>>(u.OverlayFlags, LedgerJson.Options) ?? []).Contains(OverlayFlag.AwaitingVendorSerial)).Select(u => u.UnitId).ToList();
        if (flagged.Count == 0) return new(productionOrder, 0, []);
        var identifiers = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && flagged.Contains(i.UnitId) && i.ValidTo == null).ToListAsync(cancellationToken);
        var items = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenant.TenantId && flagged.Contains(a.UnitId) && a.ValidTo == null).ToListAsync(cancellationToken);
        var allocation = created.ToDictionary(e => e.UnitId);
        var rows = flagged.Select(id => new
            {
                UnitId = id,
                Open = identifiers.Where(i => i.UnitId == id).ToList(),
                Item = items.SingleOrDefault(i => i.UnitId == id),
            })
            .Where(x => x.Open.Count == 1 && x.Open[0].Type == IdentifierType.Provisional && x.Item is not null)
            .OrderBy(x => allocation[x.UnitId].Seq)
            .Select(x => new AwaitingVendorSerialUnit(x.UnitId, x.Item!.ItemId, x.Open[0].NormalisedValue, allocation[x.UnitId].EventTime))
            .ToList();
        return new(productionOrder, rows.Count, rows);
    }
}
