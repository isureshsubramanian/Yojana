using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Identity;

/// <summary>Certificate-facing identity snapshot. Certificate composition sees this contract, never identity tables.</summary>
public sealed class CertificateUnitReadPort(SerialisationDbContext db, ITenantContext tenant)
    : ICertificateUnitReadPort
{
    public async Task<CertificateUnitFact?> ReadAsync(Guid unitId, CancellationToken cancellationToken) =>
        (await ReadManyAsync([unitId], cancellationToken)).GetValueOrDefault(unitId);

    /// <summary>
    /// One set-based read per table for the whole list; the single-unit read above goes through here too, so a migration
    /// load's certificates and a live issue compose from the same snapshot code. Rows are ordered in SQL and grouped in
    /// memory, which keeps each unit's identifier and assembly order exactly as the per-unit query gave it.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CertificateUnitFact>> ReadManyAsync(IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var requested = unitIds.Distinct().ToList();
        var units = requested.Count == 0 ? [] : await db.Units.AsNoTracking()
            .Where(row => row.TenantId == tenantId && requested.Contains(row.UnitId)).ToListAsync(cancellationToken);
        if (units.Count == 0) return new Dictionary<Guid, CertificateUnitFact>();
        var ids = units.Select(row => row.UnitId).ToList();
        var assignments = (await db.ItemAssignments.AsNoTracking()
                .Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId) && row.ValidTo == null)
                .Select(row => new { row.UnitId, row.ItemId, row.ModelVersionId }).ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.Single());
        // The creating event is found by the ledger's primary key (tenant, event time, event id) through the time the
        // unit row recorded for it; two independent IN lists over the ledger cost over a second per migration chunk.
        // A unit whose recorded time does not find its event falls back to the unit/event-id lookup.
        var commissioned = (await (from unit in db.Units.AsNoTracking()
                                   join created in db.Events.AsNoTracking()
                                       on new { unit.TenantId, EventTime = unit.CreatedEventTime, EventId = unit.CreatedEventId }
                                       equals new { created.TenantId, created.EventTime, created.EventId }
                                   where unit.TenantId == tenantId && ids.Contains(unit.UnitId) && created.UnitId == unit.UnitId
                                   select new { created.UnitId, created.EventId, created.EventTime }).ToListAsync(cancellationToken))
            .ToDictionary(row => (row.UnitId, row.EventId), row => row.EventTime);
        var unmatched = units.Where(row => !commissioned.ContainsKey((row.UnitId, row.CreatedEventId))).ToList();
        foreach (var unit in unmatched)
        {
            var eventTime = await db.Events.AsNoTracking().Where(row => row.TenantId == tenantId
                    && row.UnitId == unit.UnitId && row.EventId == unit.CreatedEventId)
                .Select(row => (DateTime?)row.EventTime).SingleOrDefaultAsync(cancellationToken);
            if (eventTime is { } time) commissioned[(unit.UnitId, unit.CreatedEventId)] = time;
        }
        var builds = (await db.BuildRecords.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId))
                .OrderByDescending(row => row.Version).ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First());
        var ownerships = (await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId)
                    && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
                .Select(row => new { row.UnitId, Fact = new CertificateOwnershipFact(row.EpisodeId, row.PartyRef, row.PartyType, row.ValidFrom) })
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.Single().Fact);
        var identifiers = (await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId))
                .OrderBy(row => row.Type).ThenBy(row => row.ValidFrom)
                .Select(row => new { row.UnitId, Fact = new CertificateUnitIdentifierFact(row.IdentifierId, row.Type, row.Scheme,
                    row.RawValue, row.IssuerPartyRef, row.ValidFrom, row.ValidTo, row.SupersedesIdentifierId,
                    row.SupersededByIdentifierId) })
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.UnitId, row => row.Fact);
        var links = (await db.AssemblyLinks.AsNoTracking()
                .Where(row => row.TenantId == tenantId && ids.Contains(row.ParentUnitId) && row.RemovedAt == null)
                .OrderBy(row => row.Slot).ThenBy(row => row.InstalledAt).ToListAsync(cancellationToken))
            .ToLookup(row => row.ParentUnitId);
        var childIds = links.SelectMany(group => group).Where(row => row.ChildUnitId != null)
            .Select(row => row.ChildUnitId!.Value).Distinct().ToList();
        var childAssignments = childIds.Count == 0 ? new Dictionary<Guid, Guid>() : await db.ItemAssignments.AsNoTracking()
            .Where(row => row.TenantId == tenantId && childIds.Contains(row.UnitId) && row.ValidTo == null)
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var childSerials = childIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenantId && childIds.Contains(row.UnitId) && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .ToDictionaryAsync(row => row.UnitId, row => row.RawValue, cancellationToken);

        var facts = new Dictionary<Guid, CertificateUnitFact>(units.Count);
        foreach (var unit in units)
        {
            if (!assignments.TryGetValue(unit.UnitId, out var assignment)) continue;
            if (!commissioned.TryGetValue((unit.UnitId, unit.CreatedEventId), out var commissionedAt))
                throw new InvalidOperationException($"Unit {unit.UnitId} has no creating event in the ledger.");
            var assembly = links[unit.UnitId].Select(row => new CertificateAssemblyFact(row.LinkId, row.Slot, row.Relation, row.ChildUnitId,
                row.ChildUnitId is { } child && childAssignments.TryGetValue(child, out var childItem) ? childItem : row.ChildItemId,
                row.ChildLotId, row.Quantity, row.ReleaseStatus, row.ReleaseDocRef, row.InstalledAt, row.RemovedAt,
                row.ChildUnitId is { } serialChild && childSerials.TryGetValue(serialChild, out var serial) ? serial : null)).ToList();
            var build = builds.GetValueOrDefault(unit.UnitId);
            var buildFact = build is null ? null : new CertificateBuildFact(build.BuildRecordId, build.Version, build.ReleasedAt,
                build.ItemId, build.ModelVersionId, build.Revision, build.FacilityId, build.Payload, build.PayloadHash, build.ReleaseSignatureRef);
            facts[unit.UnitId] = new CertificateUnitFact(unit.UnitId, unit.LifecycleStatus, unit.Origin, unit.HistoryDepth,
                assignment.ItemId, unit.LotId, commissionedAt, buildFact, ownerships.GetValueOrDefault(unit.UnitId),
                identifiers[unit.UnitId].ToList(), assembly);
        }
        return facts;
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
