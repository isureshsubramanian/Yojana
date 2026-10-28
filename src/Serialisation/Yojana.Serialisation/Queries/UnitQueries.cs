using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Portal;

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
        var parsed = ScanParser.Parse(query);
        if (parsed.Format == ScanFormat.Url) return new LookupResult(query, LookupResult.Resolutions.None, []);

        var types = identifierType is null ? LookupTypes : [identifierType];
        async Task<List<IdentifierLookupProjection>> ReadAsync(string value)
        {
            var candidates = types.Select(t => IdentifierNormaliser.Normalise(value, policies.For(t)).Value)
                .Distinct(StringComparer.Ordinal).ToList();
            return await (from i in db.Identifiers.AsNoTracking()
                          join u in db.Units.AsNoTracking() on new { i.TenantId, i.UnitId } equals new { u.TenantId, u.UnitId }
                          join a in db.ItemAssignments.AsNoTracking().Where(x => x.ValidTo == null) on new { i.TenantId, i.UnitId } equals new { a.TenantId, a.UnitId } into items
                          from a in items.DefaultIfEmpty()
                          where i.TenantId == tenantId && candidates.Contains(i.NormalisedValue) && (identifierType == null || i.Type == identifierType)
                          select new IdentifierLookupProjection
                          {
                              UnitId = i.UnitId, IdentifierId = i.IdentifierId, Type = i.Type, Scheme = i.Scheme,
                              NormalisedValue = i.NormalisedValue, RawValue = i.RawValue, ValidTo = i.ValidTo,
                              SupersededByIdentifierId = i.SupersededByIdentifierId, ItemId = (Guid?)a.ItemId,
                              LifecycleStatus = u.LifecycleStatus,
                          }).ToListAsync(cancellationToken);
        }

        var rows = await ReadAsync(parsed.Cleaned);
        foreach (var alternative in parsed.Alternatives)
        {
            if (rows.Count > 0) break;
            rows = await ReadAsync(alternative);
        }
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

    private sealed class IdentifierLookupProjection
    {
        public Guid UnitId { get; init; }
        public Guid IdentifierId { get; init; }
        public required string Type { get; init; }
        public required string Scheme { get; init; }
        public required string NormalisedValue { get; init; }
        public required string RawValue { get; init; }
        public DateTime? ValidTo { get; init; }
        public Guid? SupersededByIdentifierId { get; init; }
        public Guid? ItemId { get; init; }
        public required string LifecycleStatus { get; init; }
    }

    private sealed class UnitSearchProjection
    {
        public Guid IdentifierId { get; init; }
        public Guid UnitId { get; init; }
        public required string NormalisedValue { get; init; }
        public required string SortTie { get; init; }
        public required string RawValue { get; init; }
        public required string Type { get; init; }
        public required string Scheme { get; init; }
        public DateTime? ValidTo { get; init; }
        public Guid? SupersededByIdentifierId { get; init; }
        public Guid ItemId { get; init; }
        public Guid? ModelVersionId { get; init; }
        public required string LifecycleStatus { get; init; }
        public required string Disposition { get; init; }
        public string? LocationKind { get; init; }
        public Guid? LocationId { get; init; }
        public Guid? OwnerPartyRef { get; init; }
        public Guid? CustodyPartyRef { get; init; }
        public DateTime LastEventTime { get; init; }
    }

    private static readonly SortedPage<UnitSearchProjection, string> SearchPages = new("identifier", row => row.SortTie,
        new SortColumn<UnitSearchProjection, string>("identifier", row => row.NormalisedValue),
        new SortColumn<UnitSearchProjection, DateTime>("last_event", row => row.LastEventTime, SortDirection.Descending),
        new SortColumn<UnitSearchProjection, string>("status", row => row.LifecycleStatus));

    /// <summary>Prefix search over identifiers, joined to current state; a hit per identifier so the item column disambiguates (case 1).</summary>
    public async Task<UnitSearchResult> SearchAsync(UnitSearch search, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var tenantId = tenant.TenantId;
        var paging = SearchPages.Parse(search.Sort, search.Direction, search.Take, search.Cursor, search.Skip);
        var take = paging.Take;
        var prefixes = string.IsNullOrWhiteSpace(search.Prefix) ? [] : (search.IdentifierType is null ? LookupTypes : [search.IdentifierType]).Select(t => IdentifierNormaliser.Normalise(search.Prefix!, policies.For(t)).Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var identifiers = db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (prefixes.Count == 1) { var p = prefixes[0]; identifiers = identifiers.Where(i => i.NormalisedValue.StartsWith(p)); }
        else if (prefixes.Count > 1) { var p1 = prefixes[0]; var p2 = prefixes[1]; identifiers = identifiers.Where(i => i.NormalisedValue.StartsWith(p1) || i.NormalisedValue.StartsWith(p2)); }
        if (search.IdentifierType is not null) identifiers = identifiers.Where(i => i.Type == search.IdentifierType);
        if (!search.IncludeSuperseded) identifiers = identifiers.Where(i => i.ValidTo == null);

        var query = from i in identifiers
                    join c in db.Current.AsNoTracking() on new { i.TenantId, i.UnitId } equals new { c.TenantId, c.UnitId }
                    select new UnitSearchProjection
                    {
                        IdentifierId = i.IdentifierId, UnitId = i.UnitId, NormalisedValue = i.NormalisedValue,
                        SortTie = i.NormalisedValue + "|" + i.Type + "|" + i.UnitId.ToString(),
                        RawValue = i.RawValue, Type = i.Type, Scheme = i.Scheme, ValidTo = i.ValidTo,
                        SupersededByIdentifierId = i.SupersededByIdentifierId, ItemId = c.ItemId,
                        ModelVersionId = c.ModelVersionId, LifecycleStatus = c.LifecycleStatus,
                        Disposition = c.Disposition, LocationKind = c.LocationKind, LocationId = c.LocationId,
                        OwnerPartyRef = c.OwnerPartyRef, CustodyPartyRef = c.CustodyPartyRef,
                        LastEventTime = c.LastEventTime,
                    };
        if (search.ItemId is { } item) query = query.Where(x => x.ItemId == item);
        if (search.LifecycleStatus is { } status) query = query.Where(x => x.LifecycleStatus == status);
        if (search.OwnerPartyRef is { } owner) query = query.Where(x => x.OwnerPartyRef == owner);
        if (search.LocationId is { } location) query = query.Where(x => x.LocationId == location);
        if (search.Document is { } document) query = query.Where(x => db.Events.Any(e => e.TenantId == tenantId && e.DocumentType == document.Type && e.DocumentId == document.Id && e.UnitId == x.UnitId));

        // The extra row is the continuation signal. Do not replace this with Count/Distinct over the match set: at
        // benchmark scale those synchronous summaries each scan roughly a million identifier/current rows and hold a
        // pooled connection for tens of seconds. The screen deliberately reports a lower bound and a page sample.
        var bounded = await paging.Apply(query).Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = bounded.Count > take;
        var rows = bounded.Take(take).ToList();
        var unitIds = rows.Select(r => r.UnitId).Distinct().ToList();
        // unit_current identifies the latest state-changing fact. Joining that exact time and seq produces bounded
        // seeks and intentionally excludes state-neutral certificate/label audit events.
        var lastTypes = unitIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await (from current in db.Current.AsNoTracking()
                     join @event in db.Events.AsNoTracking()
                         on new { current.TenantId, current.UnitId, EventTime = current.LastEventTime }
                         equals new { @event.TenantId, @event.UnitId, @event.EventTime }
                     where current.TenantId == tenantId && unitIds.Contains(current.UnitId)
                         && @event.Seq == current.LastEventSeq
                     select new { current.UnitId, @event.EventType })
                .ToDictionaryAsync(row => row.UnitId, row => row.EventType, cancellationToken);
        var successors = rows.Where(r => r.SupersededByIdentifierId != null).Select(r => r.SupersededByIdentifierId!.Value).ToList();
        var resolvesTo = successors.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && successors.Contains(i.IdentifierId)).ToDictionaryAsync(i => i.IdentifierId, i => i.NormalisedValue, cancellationToken);
        var page = rows.Select(r => new UnitSearchRow(r.UnitId, r.NormalisedValue, r.Type, r.Scheme, r.ValidTo == null, r.SupersededByIdentifierId is { } s && resolvesTo.TryGetValue(s, out var v) ? v : null, r.ItemId, r.ModelVersionId, r.LifecycleStatus, r.Disposition, r.LocationKind, r.LocationId, r.OwnerPartyRef, r.CustodyPartyRef, lastTypes.TryGetValue(r.UnitId, out var lt) ? lt : null, r.LastEventTime, r.RawValue)).ToList();
        watch.Stop();
        var visited = paging.Skip + page.Count;
        var pagingTotal = visited + (hasMore ? 1 : 0);
        var sample = rows.Count > 0;
        return new UnitSearchResult(new Page<UnitSearchRow>(page, pagingTotal, paging.Skip, take), visited,
            rows.Select(row => row.UnitId).Distinct().Count(), rows.Select(row => row.ItemId).Distinct().Count(),
            rows.Select(row => row.Type).Distinct().Count(), rows.Count(row => row.ValidTo != null),
            watch.ElapsedMilliseconds, hasMore, hasMore, sample && (hasMore || paging.Skip > 0 || search.Cursor is not null),
            hasMore ? paging.CursorFor(rows[^1]) : null);
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
        // A serialised child's item lives on the child unit, not on the link (the link carries it only for lot rows).
        var childItems = childIds.Count == 0 ? new Dictionary<Guid, Guid>() : await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && childIds.Contains(a.UnitId) && a.ValidTo == null).ToDictionaryAsync(a => a.UnitId, a => a.ItemId, cancellationToken);
        var holdSources = await db.ProductionComponentHolds.AsNoTracking().Where(row => row.TenantId == tenantId
            && row.AssemblyLinkId != null && links.Select(link => link.LinkId).Contains(row.AssemblyLinkId.Value))
            .ToDictionaryAsync(row => row.AssemblyLinkId!.Value, cancellationToken);
        var asBuilt = links.Select(l =>
        {
            holdSources.TryGetValue(l.LinkId, out var source);
            return new AsBuiltRow(l.LinkId, l.Slot, l.Relation, l.ChildUnitId,
                l.ChildUnitId is { } c && childSerials.TryGetValue(c, out var s) ? s : null,
                l.ChildItemId ?? (l.ChildUnitId is { } child && childItems.TryGetValue(child, out var item) ? (Guid?)item : null),
                l.ChildLotId, l.Quantity, l.ReleaseStatus, l.ThroughPhantomItemId,
                l.InstalledAt, l.RemovedAt,
                source is null ? null : new(source.ProductionOrderType, source.ProductionOrderId),
                source?.ReceiptDocumentId is { } receiptId && source.ReceiptDocumentType is { } receiptType
                    ? new(receiptType, receiptId) : null,
                source is null ? null : new(source.IssueDocumentType, source.IssueDocumentId, source.IssueLine));
        }).ToList();
        var records = await db.BuildRecords.AsNoTracking().Where(b => b.TenantId == tenantId && b.UnitId == unitId).OrderByDescending(b => b.Version).Select(b => new BuildRecordRow(b.BuildRecordId, b.Version, b.ReleasedAt, b.PayloadHash, b.ReleaseSignatureRef)).ToListAsync(cancellationToken);
        var parent = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == unitId && l.RemovedAt == null).Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
        var eventCount = await db.Events.AsNoTracking().CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken) + await db.ArchivedEvents.AsNoTracking().CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken);
        var itemId = current?.ItemId ?? await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == unitId && a.ValidTo == null).Select(a => a.ItemId).FirstOrDefaultAsync(cancellationToken);
        var productionHold = await db.ProductionComponentHolds.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == unitId).OrderByDescending(row => row.IssuedAt).FirstOrDefaultAsync(cancellationToken);
        string? productionParentSerial = null;
        if (productionHold?.ParentUnitId is { } productionParent)
            productionParentSerial = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == productionParent && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
                .Select(row => row.RawValue).SingleOrDefaultAsync(cancellationToken);
        var productionSource = productionHold is null ? null : new ProductionSourceRow(
            new(productionHold.ProductionOrderType, productionHold.ProductionOrderId),
            new(productionHold.IssueDocumentType, productionHold.IssueDocumentId, productionHold.IssueLine),
            productionHold.ReceiptDocumentId is { } sourceReceiptId && productionHold.ReceiptDocumentType is { } sourceReceiptType
                ? new(sourceReceiptType, sourceReceiptId) : null,
            productionHold.ParentUnitId, productionParentSerial);
        return new UnitDetail(unit.UnitId, unit.LifecycleStatus, unit.Origin, unit.SerialisationPoint, unit.RetentionClass, unit.HistoryDepth, unit.LotId, unit.ExpiryDate, unit.DerivedFromUnitId, unit.MergedIntoUnitId,
            itemId, current?.ModelVersionId, current?.Disposition ?? UnitLifecycle.DispositionFor(unit.LifecycleStatus), current?.ConditionGrade, current?.LocationId, current?.LocationKind, current?.CustodyPartyRef, current?.OwnerPartyRef, current?.OpenOwnershipEpisodeId, current?.OpenCustodyEpisodeId,
            current?.OverlayFlags ?? unit.OverlayFlags, current?.LastEventSeq ?? 0, current?.LastEventTime ?? unit.CreatedAt, identifiers, episodes, asBuilt, records, parent, eventCount,
            CustomerAssertionCustodyRule.OwnerAndHolderDiffer(current?.OwnerPartyRef, current?.CustodyPartyRef,
                current?.LocationKind), productionSource);
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
