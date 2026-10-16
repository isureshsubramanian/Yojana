using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Queries;

/// <summary>
/// Unit search and lookup (MOD-03 queries; ADR-0001 "Every serial list pages server-side"). An exact identifier is a
/// seek on the open-identifier index and opens the unit; a superseded identifier resolves to its unit and says so; the
/// same string on two items is reported as ambiguous with both; a prefix is <c>LIKE 'value%'</c> on the covering lookup
/// index, filtered and paged on the server. The detail query returns what the approved Unit detail screen shows.
/// </summary>
public sealed class UnitQueries(SerialisationDbContext db, IIdentifierPolicySource policies, ITenantContext tenant)
{
    private static readonly string[] LookupTypes = [IdentifierType.InternalSerial, IdentifierType.ManufacturerSerial, IdentifierType.VendorSerial, IdentifierType.CustomerSerial, IdentifierType.Sgtin, IdentifierType.Giai, IdentifierType.Imei, IdentifierType.Mac, IdentifierType.Vin, IdentifierType.UdiDi, IdentifierType.Uii, IdentifierType.CustomerAssetTag, IdentifierType.ChannelOrderRef, IdentifierType.SoftwareLicense, IdentifierType.Provisional];

    /// <summary>Exact resolution of a scanned or typed identifier, trying each type's normalisation.</summary>
    public async Task<LookupResult> LookupAsync(string query, string? identifierType, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var candidates = (identifierType is null ? LookupTypes : [identifierType]).Select(t => IdentifierNormaliser.Normalise(query, policies.For(t)).Value).Distinct(StringComparer.Ordinal).ToList();
        var rows = await (from i in db.Identifiers.AsNoTracking()
                          join u in db.Units.AsNoTracking() on new { i.TenantId, i.UnitId } equals new { u.TenantId, u.UnitId }
                          join a in db.ItemAssignments.AsNoTracking().Where(x => x.ValidTo == null) on new { i.TenantId, i.UnitId } equals new { a.TenantId, a.UnitId } into items
                          from a in items.DefaultIfEmpty()
                          where i.TenantId == tenantId && candidates.Contains(i.NormalisedValue) && (identifierType == null || i.Type == identifierType)
                          select new { i.UnitId, i.IdentifierId, i.Type, i.Scheme, i.NormalisedValue, i.RawValue, i.ValidTo, i.SupersededByIdentifierId, ItemId = (Guid?)a.ItemId, u.LifecycleStatus })
                         .ToListAsync(cancellationToken);
        var successors = rows.Where(r => r.SupersededByIdentifierId != null).Select(r => r.SupersededByIdentifierId!.Value).ToList();
        var resolvesTo = successors.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && successors.Contains(i.IdentifierId)).ToDictionaryAsync(i => i.IdentifierId, i => i.NormalisedValue, cancellationToken);
        var hits = rows.Select(r => new LookupHit(r.UnitId, r.IdentifierId, r.Type, r.Scheme, r.NormalisedValue, r.RawValue, r.ValidTo == null, r.SupersededByIdentifierId, r.SupersededByIdentifierId is { } s && resolvesTo.TryGetValue(s, out var v) ? v : null, r.ItemId, r.LifecycleStatus)).ToList();
        var open = hits.Where(h => h.Open).ToList();
        var resolution = open.Select(h => h.UnitId).Distinct().Count() switch
        {
            1 => LookupResult.Resolutions.Exact,
            > 1 => LookupResult.Resolutions.Ambiguous,
            _ => hits.Count > 0 ? LookupResult.Resolutions.Superseded : LookupResult.Resolutions.None,
        };
        return new LookupResult(query, resolution, hits.OrderByDescending(h => h.Open).ThenBy(h => h.NormalisedValue).ToList());
    }

    /// <summary>Prefix search over identifiers, joined to current state; a hit per identifier so the item column disambiguates (case 1).</summary>
    public async Task<UnitSearchResult> SearchAsync(UnitSearch search, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var tenantId = tenant.TenantId;
        var take = Math.Clamp(search.Take, 1, 500);
        var prefixes = string.IsNullOrWhiteSpace(search.Prefix) ? [] : (search.IdentifierType is null ? LookupTypes : [search.IdentifierType]).Select(t => IdentifierNormaliser.Normalise(search.Prefix!, policies.For(t)).Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var identifiers = db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (prefixes.Count == 1) { var p = prefixes[0]; identifiers = identifiers.Where(i => i.NormalisedValue.StartsWith(p)); }
        else if (prefixes.Count > 1) { var p1 = prefixes[0]; var p2 = prefixes[1]; identifiers = identifiers.Where(i => i.NormalisedValue.StartsWith(p1) || i.NormalisedValue.StartsWith(p2)); }
        if (search.IdentifierType is not null) identifiers = identifiers.Where(i => i.Type == search.IdentifierType);
        if (!search.IncludeSuperseded) identifiers = identifiers.Where(i => i.ValidTo == null);

        var query = from i in identifiers
                    join c in db.Current.AsNoTracking() on new { i.TenantId, i.UnitId } equals new { c.TenantId, c.UnitId }
                    select new { i, c };
        if (search.ItemId is { } item) query = query.Where(x => x.c.ItemId == item);
        if (search.LifecycleStatus is { } status) query = query.Where(x => x.c.LifecycleStatus == status);
        if (search.OwnerPartyRef is { } owner) query = query.Where(x => x.c.OwnerPartyRef == owner);
        if (search.LocationId is { } location) query = query.Where(x => x.c.LocationId == location);
        if (search.Document is { } document) query = query.Where(x => db.Events.Any(e => e.TenantId == tenantId && e.DocumentType == document.Type && e.DocumentId == document.Id && e.UnitId == x.c.UnitId));

        var total = await query.CountAsync(cancellationToken);
        var stats = await query.GroupBy(_ => 1).Select(g => new { Units = g.Select(x => x.i.UnitId).Distinct().Count(), Items = g.Select(x => x.c.ItemId).Distinct().Count(), Types = g.Select(x => x.i.Type).Distinct().Count(), Superseded = g.Count(x => x.i.ValidTo != null) }).FirstOrDefaultAsync(cancellationToken);
        var ordered = search.Sort == "last_event" ? query.OrderByDescending(x => x.c.LastEventTime).ThenBy(x => x.i.NormalisedValue) : query.OrderBy(x => x.i.NormalisedValue).ThenBy(x => x.i.Type);
        var rows = await ordered.Skip(search.Skip).Take(take).Select(x => new { x.i.UnitId, x.i.NormalisedValue, x.i.RawValue, x.i.Type, x.i.Scheme, x.i.ValidTo, x.i.SupersededByIdentifierId, x.c.ItemId, x.c.ModelVersionId, x.c.LifecycleStatus, x.c.Disposition, x.c.LocationKind, x.c.LocationId, x.c.OwnerPartyRef, x.c.CustodyPartyRef, x.c.LastEventTime }).ToListAsync(cancellationToken);
        var unitIds = rows.Select(r => r.UnitId).Distinct().ToList();
        var lastTypes = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && unitIds.Contains(e.UnitId)).GroupBy(e => e.UnitId).Select(g => new { UnitId = g.Key, Type = g.OrderByDescending(e => e.Seq).Select(e => e.EventType).First() }).ToDictionaryAsync(x => x.UnitId, x => x.Type, cancellationToken);
        var successors = rows.Where(r => r.SupersededByIdentifierId != null).Select(r => r.SupersededByIdentifierId!.Value).ToList();
        var resolvesTo = successors.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && successors.Contains(i.IdentifierId)).ToDictionaryAsync(i => i.IdentifierId, i => i.NormalisedValue, cancellationToken);
        var page = rows.Select(r => new UnitSearchRow(r.UnitId, r.NormalisedValue, r.Type, r.Scheme, r.ValidTo == null, r.SupersededByIdentifierId is { } s && resolvesTo.TryGetValue(s, out var v) ? v : null, r.ItemId, r.ModelVersionId, r.LifecycleStatus, r.Disposition, r.LocationKind, r.LocationId, r.OwnerPartyRef, r.CustodyPartyRef, lastTypes.TryGetValue(r.UnitId, out var lt) ? lt : null, r.LastEventTime, r.RawValue)).ToList();
        watch.Stop();
        return new UnitSearchResult(new Page<UnitSearchRow>(page, total, search.Skip, take), total, stats?.Units ?? 0, stats?.Items ?? 0, stats?.Types ?? 0, stats?.Superseded ?? 0, watch.ElapsedMilliseconds);
    }

    public async Task<UnitDetail?> DetailAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == unitId, cancellationToken);
        if (unit is null) return null;
        var current = await db.Current.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UnitId == unitId, cancellationToken);
        var identifiers = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && i.UnitId == unitId).OrderBy(i => i.ValidTo == null ? 0 : 1).ThenBy(i => i.Type).ThenByDescending(i => i.ValidFrom)
            .Select(i => new IdentifierRow(i.IdentifierId, i.Type, i.Scheme, i.RawValue, i.NormalisedValue, i.ScopeKey, i.IssuerPartyRef, i.Qualifier, i.ValidFrom, i.ValidTo, i.SupersededByIdentifierId, i.SupersedesIdentifierId, i.CorrectionReason, i.ActorRef)).ToListAsync(cancellationToken);
        var episodes = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).OrderByDescending(e => e.ValidFrom).Take(50)
            .Select(e => new EpisodeRow(e.EpisodeId, e.Kind, e.PartyRef, e.PartyType, e.LocationId, e.LocationKind, e.Reason, e.Source, e.Confidence, e.ValidFrom, e.ValidTo,
                e.OpeningDocumentType == null || e.OpeningDocumentId == null ? null : new DocumentRef(e.OpeningDocumentType, e.OpeningDocumentId.Value, e.OpeningDocumentLine),
                e.ClosingDocumentType == null || e.ClosingDocumentId == null ? null : new DocumentRef(e.ClosingDocumentType, e.ClosingDocumentId.Value, e.ClosingDocumentLine),
                e.CustomerPoRef, e.CustomerItemRef, e.DueBack)).ToListAsync(cancellationToken);
        var links = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ParentUnitId == unitId).OrderBy(l => l.RemovedAt == null ? 0 : 1).ThenBy(l => l.Slot).ToListAsync(cancellationToken);
        var childIds = links.Where(l => l.ChildUnitId != null).Select(l => l.ChildUnitId!.Value).ToList();
        var childSerials = childIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && childIds.Contains(i.UnitId) && i.ValidTo == null && i.Type == IdentifierType.InternalSerial).ToDictionaryAsync(i => i.UnitId, i => i.NormalisedValue, cancellationToken);
        var asBuilt = links.Select(l => new AsBuiltRow(l.LinkId, l.Slot, l.Relation, l.ChildUnitId, l.ChildUnitId is { } c && childSerials.TryGetValue(c, out var s) ? s : null, l.ChildItemId, l.ChildLotId, l.Quantity, l.ReleaseStatus, l.ThroughPhantomItemId, l.InstalledAt, l.RemovedAt)).ToList();
        var records = await db.BuildRecords.AsNoTracking().Where(b => b.TenantId == tenantId && b.UnitId == unitId).OrderByDescending(b => b.Version).Select(b => new BuildRecordRow(b.BuildRecordId, b.Version, b.ReleasedAt, b.PayloadHash, b.ReleaseSignatureRef)).ToListAsync(cancellationToken);
        var parent = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == unitId && l.RemovedAt == null).Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
        var eventCount = await db.Events.AsNoTracking().CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken) + await db.ArchivedEvents.AsNoTracking().CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken);
        var itemId = current?.ItemId ?? await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == unitId && a.ValidTo == null).Select(a => a.ItemId).FirstOrDefaultAsync(cancellationToken);
        return new UnitDetail(unit.UnitId, unit.LifecycleStatus, unit.Origin, unit.SerialisationPoint, unit.RetentionClass, unit.HistoryDepth, unit.LotId, unit.ExpiryDate, unit.DerivedFromUnitId, unit.MergedIntoUnitId,
            itemId, current?.ModelVersionId, current?.Disposition ?? UnitLifecycle.DispositionFor(unit.LifecycleStatus), current?.ConditionGrade, current?.LocationId, current?.LocationKind, current?.CustodyPartyRef, current?.OwnerPartyRef, current?.OpenOwnershipEpisodeId, current?.OpenCustodyEpisodeId,
            current?.OverlayFlags ?? unit.OverlayFlags, current?.LastEventSeq ?? 0, current?.LastEventTime ?? unit.CreatedAt, identifiers, episodes, asBuilt, records, parent, eventCount);
    }

    /// <summary>History page in the ADR order (business time, recorded time, seq), newest first for the screen; live and archive together.</summary>
    public async Task<Page<HistoryRow>> HistoryAsync(Guid unitId, int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        take = Math.Clamp(take, 1, 200);
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).Select(e => new HistoryRow(e.EventId, e.Seq, e.EventType, e.EventTime, e.RecordedTime, e.BizStep, e.Disposition, e.LocationId, e.ActorRef, e.DocumentType == null || e.DocumentId == null ? null : new DocumentRef(e.DocumentType, e.DocumentId.Value, e.DocumentLine), e.Attributes)).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).Select(e => new HistoryRow(e.EventId, e.Seq, e.EventType, e.EventTime, e.RecordedTime, e.BizStep, e.Disposition, e.LocationId, e.ActorRef, e.DocumentType == null || e.DocumentId == null ? null : new DocumentRef(e.DocumentType, e.DocumentId.Value, e.DocumentLine), e.Attributes)).ToListAsync(cancellationToken);
        var all = live.Concat(archived).OrderByDescending(e => e.EventTime).ThenByDescending(e => e.RecordedTime).ThenByDescending(e => e.Seq).ToList();
        return new Page<HistoryRow>(all.Skip(skip).Take(take).ToList(), all.Count, skip, take);
    }
}
