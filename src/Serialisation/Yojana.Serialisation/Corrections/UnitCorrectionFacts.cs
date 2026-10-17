using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Corrections;

/// <summary>Ledger facts shared by corrections whose rules distinguish shipping from other external documents.</summary>
public static class UnitCorrectionFacts
{
    // The vocabulary is deliberately conservative: an unknown document kind is external. These are the module's
    // internal manufacturing, stock and audit documents; adding another internal kind is an explicit boundary change.
    private static readonly HashSet<string> InternalDocumentTypes = new(StringComparer.Ordinal)
    {
        "production_order", "production_receipt", "adjustment", "transfer", "migration", "correction", "backfill_batch",
    };

    public sealed record DocumentFacts(bool EverShipped, bool AppearsOnExternalDocument);

    public static bool IsExternalDocument(string? documentType) =>
        documentType is not null && !InternalDocumentTypes.Contains(documentType);

    public static async Task<DocumentFacts> ReadAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, CancellationToken cancellationToken)
    {
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId)
            .Select(e => new { e.EventType, e.DocumentType }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId)
            .Select(e => new { e.EventType, e.DocumentType }).ToListAsync(cancellationToken);
        return new(
            live.Any(e => e.EventType == UnitEventType.Shipped) || archived.Any(e => e.EventType == UnitEventType.Shipped),
            live.Any(e => IsExternalDocument(e.DocumentType)) || archived.Any(e => IsExternalDocument(e.DocumentType)));
    }
}
