using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Identity;

/// <summary>Certificate-facing identity snapshot. Certificate composition sees this contract, never identity tables.</summary>
public sealed class CertificateUnitReadPort(SerialisationDbContext db, ITenantContext tenant)
    : ICertificateUnitReadPort
{
    public async Task<CertificateUnitFact?> ReadAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.UnitId == unitId, cancellationToken);
        if (unit is null) return null;
        var assignment = await db.ItemAssignments.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.ValidTo == null)
            .Select(row => new { row.ItemId, row.ModelVersionId }).SingleOrDefaultAsync(cancellationToken);
        if (assignment is null) return null;
        var build = await db.BuildRecords.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId)
            .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
        var ownership = await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId
                && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
            .Select(row => new CertificateOwnershipFact(row.EpisodeId, row.PartyRef, row.PartyType, row.ValidFrom))
            .SingleOrDefaultAsync(cancellationToken);
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId)
            .OrderBy(row => row.Type).ThenBy(row => row.ValidFrom)
            .Select(row => new CertificateUnitIdentifierFact(row.IdentifierId, row.Type, row.Scheme, row.RawValue,
                row.IssuerPartyRef, row.ValidFrom, row.ValidTo, row.SupersedesIdentifierId, row.SupersededByIdentifierId))
            .ToListAsync(cancellationToken);
        var links = await db.AssemblyLinks.AsNoTracking().Where(row => row.TenantId == tenantId && row.ParentUnitId == unitId && row.RemovedAt == null)
            .OrderBy(row => row.Slot).ThenBy(row => row.InstalledAt).ToListAsync(cancellationToken);
        var childIds = links.Where(row => row.ChildUnitId != null).Select(row => row.ChildUnitId!.Value).Distinct().ToList();
        var childAssignments = childIds.Count == 0 ? new Dictionary<Guid, Guid>() : await db.ItemAssignments.AsNoTracking()
            .Where(row => row.TenantId == tenantId && childIds.Contains(row.UnitId) && row.ValidTo == null)
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var childSerials = childIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenantId && childIds.Contains(row.UnitId) && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .ToDictionaryAsync(row => row.UnitId, row => row.RawValue, cancellationToken);
        var assembly = links.Select(row => new CertificateAssemblyFact(row.LinkId, row.Slot, row.Relation, row.ChildUnitId,
            row.ChildUnitId is { } child && childAssignments.TryGetValue(child, out var childItem) ? childItem : row.ChildItemId,
            row.ChildLotId, row.Quantity, row.ReleaseStatus, row.ReleaseDocRef, row.InstalledAt, row.RemovedAt,
            row.ChildUnitId is { } serialChild && childSerials.TryGetValue(serialChild, out var serial) ? serial : null)).ToList();
        var buildFact = build is null ? null : new CertificateBuildFact(build.BuildRecordId, build.Version, build.ReleasedAt,
            build.ItemId, build.ModelVersionId, build.Revision, build.FacilityId, build.Payload, build.PayloadHash, build.ReleaseSignatureRef);
        return new CertificateUnitFact(unitId, unit.LifecycleStatus, unit.Origin, unit.HistoryDepth, assignment.ItemId,
            unit.LotId, buildFact, ownership, identifiers, assembly);
    }

    public Task<IReadOnlyList<Guid>> FindUnitIdsBySerialAsync(string serial, CancellationToken cancellationToken) =>
        db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.RawValue.Contains(serial) || row.NormalisedValue.Contains(serial)) && row.ValidTo == null)
            .Select(row => row.UnitId).Distinct().ToListAsync(cancellationToken)
            .ContinueWith<IReadOnlyList<Guid>>(task => task.Result, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async Task<IReadOnlyDictionary<Guid, string>> PrimarySerialsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        var rows = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && unitIds.Contains(row.UnitId) && row.ValidTo == null)
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(row => row.Type)
            .Select(row => new { row.UnitId, row.RawValue }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First().RawValue);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> CurrentItemIdsAsync(IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken) => await db.ItemAssignments.AsNoTracking()
        .Where(row => row.TenantId == tenant.TenantId && unitIds.Contains(row.UnitId) && row.ValidTo == null)
        .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
}
