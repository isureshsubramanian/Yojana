using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>Question-record boundary over serialisation-owned read models.</summary>
public sealed class SerialisationRecordReferenceResolver(SerialisationDbContext db, string recordKind)
    : IRecordReferenceResolver
{
    public async Task<IReadOnlyDictionary<Guid, RecordReference>> ResolveAsync(IReadOnlyCollection<Guid> recordIds,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        if (recordIds.Count > IRecordReferenceResolver.MaxRecordIds)
            throw new ArgumentException($"At most {IRecordReferenceResolver.MaxRecordIds} record ids resolve in one call.", nameof(recordIds));
        var ids = recordIds.Distinct().ToList();
        var mayView = viewer.Roles.Any(YojanaRole.IsKnown);
        var labels = recordKind switch
        {
            RecordKind.Unit => await UnitLabelsAsync(ids, cancellationToken),
            RecordKind.Claim => await db.Claims.AsNoTracking().Where(x => ids.Contains(x.ClaimId))
                .Select(x => new { Id = x.ClaimId, Label = x.ClaimNumber }).ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken),
            RecordKind.Rma => await db.Rmas.AsNoTracking().Where(x => ids.Contains(x.RmaId))
                .Select(x => new { Id = x.RmaId, Label = x.RmaNumber ?? "RMA pending number" }).ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken),
            RecordKind.RefurbishmentWorkOrder => await db.RefurbishmentWorkOrders.AsNoTracking().Where(x => ids.Contains(x.WorkOrderId))
                .Select(x => new { Id = x.WorkOrderId, Label = x.WorkOrderNumber }).ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken),
            RecordKind.WarrantyPolicy => await db.PolicyVersions.AsNoTracking().Where(x => ids.Contains(x.PolicyVersionId))
                .Select(x => new { Id = x.PolicyVersionId, Label = x.Name + " v" + x.Version }).ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken),
            RecordKind.Certificate => await db.CertificateInstances.AsNoTracking().Where(x => ids.Contains(x.CertificateId))
                .Select(x => new { Id = x.CertificateId, Label = x.TrackingNumber }).ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken),
            RecordKind.ImportJob => await db.ImportJobs.AsNoTracking().Where(x => ids.Contains(x.JobId))
                .Select(x => new { Id = x.JobId, Label = x.FileName }).ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken),
            _ => throw new InvalidOperationException($"'{recordKind}' is not owned by Serialisation."),
        };
        return ids.ToDictionary(id => id, id => labels.TryGetValue(id, out var label)
            ? new RecordReference(id, true, label, mayView)
            : new RecordReference(id, false, null, false));
    }

    private async Task<Dictionary<Guid, string>> UnitLabelsAsync(IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var units = await db.Units.AsNoTracking().Where(x => ids.Contains(x.UnitId)).Select(x => x.UnitId)
            .ToListAsync(cancellationToken);
        var identifiers = await db.Identifiers.AsNoTracking().Where(x => ids.Contains(x.UnitId) && x.ValidTo == null)
            .OrderBy(x => x.Type).Select(x => new { x.UnitId, x.NormalisedValue }).ToListAsync(cancellationToken);
        return units.ToDictionary(id => id, id => identifiers.FirstOrDefault(x => x.UnitId == id)?.NormalisedValue
            ?? "Unidentified unit");
    }
}
