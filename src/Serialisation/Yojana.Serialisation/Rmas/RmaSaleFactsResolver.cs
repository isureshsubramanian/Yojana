using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Rmas;

public sealed record RmaSaleFacts(Guid OwnershipEpisodeId, DocumentRef OriginalSaleRef, Guid? ParentUnitId, decimal? PriceAllocation);

/// <summary>Resolves immutable sale linkage from the current owner episode and latest shipment; callers never supply it.</summary>
public sealed class RmaSaleFactsResolver(SerialisationDbContext db, ISalesDocumentQueryPort sales, ITenantContext tenant)
{
    public async Task<RmaSaleFacts> ResolveAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ownership = await db.Episodes.TagWith("P6-T22 RMA create: current ownership").AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null)
            .Select(e => new { e.EpisodeId, e.ValidFrom, e.OpeningDocumentType, e.OpeningDocumentId, e.OpeningDocumentLine })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("current_ownership_required", $"Unit {unitId} has no current ownership episode.", "record the current owner before requesting its return");

        // Project before ordering: constructing DocumentRef inside the query does not translate on SQL Server.
        var shipment = await db.Events.TagWith("P6-T22 RMA create: latest shipment").AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.EventType == UnitEventType.Shipped
                && e.EventTime <= ownership.ValidFrom && e.DocumentType != null && e.DocumentId != null)
            .OrderByDescending(e => e.EventTime).ThenByDescending(e => e.Seq)
            .Select(e => new { Type = e.DocumentType!, Id = e.DocumentId!.Value, e.DocumentLine })
            .FirstOrDefaultAsync(cancellationToken);

        var original = shipment is not null
            ? new DocumentRef(shipment.Type, shipment.Id, shipment.DocumentLine)
            : ownership.OpeningDocumentType is not null && ownership.OpeningDocumentId is { } openingId
                ? new DocumentRef(ownership.OpeningDocumentType, openingId, ownership.OpeningDocumentLine)
                : throw new CommandRefusedException("original_sale_not_resolved", $"Unit {unitId} has no shipment or ownership source document for its current ownership episode.");

        var parent = await db.AssemblyLinks.TagWith("P6-T22 RMA create: assembly parent").AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.ChildUnitId == unitId && l.RemovedAt == null)
            .Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
        var document = await sales.GetShipmentAsync(original, cancellationToken);
        var price = document?.Lines.FirstOrDefault(l => original.Line is null || l.LineNo == original.Line)?.PriceAllocation;
        return new(ownership.EpisodeId, original, parent, price);
    }
}
