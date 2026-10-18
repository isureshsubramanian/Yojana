using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Portal;

public sealed class PortalQueries(SerialisationDbContext db, ITenantContext tenant, PortalOwnershipScope ownership,
    IIdentifierPolicySource identifierPolicies, IItemNamePort itemNames, IPartyNamePort partyNames, IClock clock)
{
    private const int MaximumPastedIdentifiers = 500;
    private static readonly string[] LookupTypes =
    [
        IdentifierType.InternalSerial, IdentifierType.ManufacturerSerial, IdentifierType.VendorSerial,
        IdentifierType.CustomerSerial, IdentifierType.Sgtin, IdentifierType.Giai, IdentifierType.Imei,
        IdentifierType.Mac, IdentifierType.Vin, IdentifierType.UdiDi, IdentifierType.UdiPi, IdentifierType.Uii,
        IdentifierType.CustomerAssetTag, IdentifierType.ChannelOrderRef, IdentifierType.SoftwareLicense,
    ];

    public async Task<PortalLookupResponse> LookupAsync(PortalPrincipal principal, string value, string? type,
        CancellationToken cancellationToken)
    {
        ownership.EnsureTenant(principal);
        var validation = Validate(new PortalSerialInput(value, type));
        if (validation.Reasons.Count > 0)
            return new(PortalQueryOutcome.Invalid, []);
        var ids = await ResolveUnitIdsAsync(principal, validation.Candidates, cancellationToken);
        if (ids.Count == 0) return new(PortalQueryOutcome.NotFound, []);
        if (ids.Count > 1) return new(PortalQueryOutcome.Ambiguous, []);
        var summaries = await SummariesAsync(principal, ids, includeClosedCoverage: true, cancellationToken);
        return new(PortalQueryOutcome.Found, summaries);
    }

    public async Task<PortalInstalledBasePage> InstalledBaseAsync(PortalPrincipal principal, bool includeComponents,
        int take, DateTime? afterDeliveryDate, Guid? afterUnitId, CancellationToken cancellationToken)
    {
        ownership.EnsureTenant(principal);
        take = Math.Clamp(take, 1, 200);
        var tenantId = tenant.TenantId;
        var roots = from episode in db.Episodes.AsNoTracking()
                    where episode.TenantId == tenantId && episode.Kind == EpisodeKind.Ownership
                        && episode.PartyRef == principal.PartyRef && episode.ValidTo == null
                        && !db.AssemblyLinks.Any(link => link.TenantId == tenantId
                            && link.ChildUnitId == episode.UnitId && link.RemovedAt == null)
                    select new InstalledBaseKey
                    {
                        UnitId = episode.UnitId,
                        DeliveryDate = episode.ValidFrom,
                        CustomerPoRef = episode.CustomerPoRef,
                        CustomerItemRef = episode.CustomerItemRef,
                        IsComponent = false,
                    };
        var keys = await PageKeysAsync(roots, take + 1, afterDeliveryDate, afterUnitId, cancellationToken);
        if (includeComponents)
        {
            var components = from episode in db.Episodes.AsNoTracking()
                             join link in db.AssemblyLinks.AsNoTracking()
                                 on new { episode.TenantId, ParentUnitId = episode.UnitId }
                                 equals new { link.TenantId, link.ParentUnitId }
                             where episode.TenantId == tenantId && episode.Kind == EpisodeKind.Ownership
                                 && episode.PartyRef == principal.PartyRef && episode.ValidTo == null
                                 && link.ChildUnitId != null && link.RemovedAt == null
                             select new InstalledBaseKey
                             {
                                 UnitId = link.ChildUnitId ?? Guid.Empty,
                                 DeliveryDate = episode.ValidFrom,
                                 CustomerPoRef = episode.CustomerPoRef,
                                 CustomerItemRef = episode.CustomerItemRef,
                                 IsComponent = true,
                             };
            keys.AddRange(await PageKeysAsync(components.Distinct(), take + 1, afterDeliveryDate, afterUnitId,
                cancellationToken));
        }
        keys = keys.OrderBy(row => row.DeliveryDate).ThenBy(row => row.UnitId)
            .DistinctBy(row => row.UnitId).Take(take + 1).ToList();
        var hasMore = keys.Count > take;
        keys = keys.Take(take).ToList();
        var summaries = (await SummariesAsync(principal, keys.Select(row => row.UnitId).Distinct().ToList(),
            includeClosedCoverage: false, cancellationToken)).ToDictionary(row => row.UnitId);
        var rows = keys.Where(row => summaries.ContainsKey(row.UnitId)).Select(row =>
        {
            var unit = summaries[row.UnitId];
            return new PortalInstalledBaseRow(row.UnitId, unit.Serial, unit.ItemCode, unit.ItemName,
                row.DeliveryDate, row.CustomerPoRef, row.CustomerItemRef, row.IsComponent, unit.Coverage);
        }).ToList();
        var last = keys.LastOrDefault();
        return new(rows, hasMore, hasMore ? last?.DeliveryDate : null, hasMore ? last?.UnitId : null);
    }

    public async IAsyncEnumerable<PortalInstalledBaseRow> ExportInstalledBaseAsync(PortalPrincipal principal,
        bool includeComponents, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DateTime? afterDate = null;
        Guid? afterId = null;
        while (true)
        {
            var page = await InstalledBaseAsync(principal, includeComponents, 200, afterDate, afterId,
                cancellationToken);
            foreach (var row in page.Rows) yield return row;
            if (!page.HasMore) yield break;
            afterDate = page.NextDeliveryDate;
            afterId = page.NextUnitId;
        }
    }

    public async Task<PortalServiceHistoryPage?> ServiceHistoryAsync(PortalPrincipal principal, Guid unitId,
        int take, DateTime? beforeAt, long? beforeSequence, CancellationToken cancellationToken)
    {
        var windows = await ownership.WindowsAsync(principal, unitId, cancellationToken);
        if (windows.Count == 0) return null;
        take = Math.Clamp(take, 1, 200);
        var rows = new List<ServiceEvent>();
        foreach (var window in windows)
        {
            rows.AddRange(await ServiceEventsAsync(db.Events.AsNoTracking(), unitId, window, take + 1,
                beforeAt, beforeSequence, cancellationToken));
            rows.AddRange(await ServiceEventsAsync(db.ArchivedEvents.AsNoTracking(), unitId, window, take + 1,
                beforeAt, beforeSequence, cancellationToken));
        }
        var ordered = rows.DistinctBy(row => row.EventId).OrderByDescending(row => row.At)
            .ThenByDescending(row => row.Sequence).Take(take + 1).ToList();
        var hasMore = ordered.Count > take;
        var selected = ordered.Take(take).ToList();
        var last = selected.LastOrDefault();
        return new(selected.Select(row => new PortalServiceHistoryRow(row.EventId, row.EventType, row.At,
            row.Status)).ToList(), hasMore, hasMore ? last?.At : null, hasMore ? last?.Sequence : null);
    }

    public async Task<PortalSerialListResult> PastedSerialsAsync(PortalPrincipal principal,
        PortalSerialListRequest request, CancellationToken cancellationToken)
    {
        ownership.EnsureTenant(principal);
        if (request.Serials.Count > MaximumPastedIdentifiers)
            throw new ArgumentException($"At most {MaximumPastedIdentifiers} identifiers may be pasted at once.",
                nameof(request));
        var checkedInputs = request.Serials.Select(input => (Input: input, Validation: Validate(input))).ToList();
        var invalid = checkedInputs.Where(row => row.Validation.Reasons.Count > 0)
            .Select(row => new PortalInvalidSerial(row.Input.Value, row.Input.IdentifierType,
                row.Validation.Reasons)).ToList();
        var valid = checkedInputs.Where(row => row.Validation.Reasons.Count == 0).ToList();
        var candidates = valid.SelectMany(row => row.Validation.Candidates).Distinct().ToList();
        var accessible = ownership.AccessibleUnitIds(principal);
        var hits = candidates.Count == 0 ? [] : await db.Identifiers.AsNoTracking()
            .MatchExact(tenant.TenantId, candidates).Where(row => accessible.Contains(row.UnitId))
            .Select(row => new IdentifierHit(row.UnitId, row.Type, row.NormalisedValue)).ToListAsync(cancellationToken);
        var matchedIds = hits.Select(row => row.UnitId).Distinct().ToList();
        var summaries = (await SummariesAsync(principal, matchedIds, includeClosedCoverage: true,
            cancellationToken)).ToDictionary(row => row.UnitId);
        var matched = new List<PortalSerialMatch>();
        var unmatched = new List<string>();
        foreach (var row in valid)
        {
            var units = hits.Where(hit => row.Validation.Candidates.Any(candidate => candidate.Value == hit.Value
                    && candidate.Type == hit.Type))
                .Select(hit => hit.UnitId).Distinct().ToList();
            if (units.Count == 1 && summaries.TryGetValue(units[0], out var summary))
                matched.Add(new PortalSerialMatch(row.Input.Value, summary));
            else
                unmatched.Add(row.Input.Value);
        }
        return new(matched, unmatched, invalid);
    }

    public async Task<PortalWhereUsedResult> WhereUsedAsync(PortalPrincipal principal, PortalSerialInput input,
        CancellationToken cancellationToken)
    {
        ownership.EnsureTenant(principal);
        var validation = Validate(input);
        if (validation.Reasons.Count > 0)
            return new(PortalQueryOutcome.Invalid, validation.Reasons, null, [], []);
        var accessible = ownership.AccessibleUnitIds(principal);
        var componentIds = await db.Identifiers.AsNoTracking().MatchExact(tenant.TenantId, validation.Candidates)
            .Where(row => accessible.Contains(row.UnitId))
            .Select(row => row.UnitId).Distinct().Take(2).ToListAsync(cancellationToken);
        if (componentIds.Count == 0) return new(PortalQueryOutcome.NotFound, [], null, [], []);
        if (componentIds.Count > 1) return new(PortalQueryOutcome.Ambiguous, [], null, [], []);
        var componentId = componentIds[0];
        var links = await db.AssemblyLinks.AsNoTracking().Where(link => link.TenantId == tenant.TenantId
                && link.ChildUnitId == componentId).OrderByDescending(link => link.InstalledAt)
            .Select(link => new { link.ParentUnitId, link.InstalledAt, link.RemovedAt }).ToListAsync(cancellationToken);
        var authorisedLinks = new List<(Guid ParentId, DateTime InstalledAt, DateTime? RemovedAt)>();
        foreach (var link in links)
        {
            var windows = await ownership.WindowsAsync(principal, link.ParentUnitId, cancellationToken);
            if (windows.Any(window => (window.To is null || link.InstalledAt < window.To)
                && (link.RemovedAt is null || link.RemovedAt > window.From)))
                authorisedLinks.Add((link.ParentUnitId, link.InstalledAt, link.RemovedAt));
        }
        if (authorisedLinks.Count == 0) return new(PortalQueryOutcome.NotFound, [], null, [], []);
        var parentIds = authorisedLinks.Select(row => row.ParentId).Distinct().ToList();
        var parentSummaries = (await SummariesAsync(principal, parentIds, includeClosedCoverage: false,
            cancellationToken)).ToDictionary(row => row.UnitId);
        var parents = authorisedLinks.Where(link => parentSummaries.ContainsKey(link.ParentId)).Select(link =>
        {
            var parent = parentSummaries[link.ParentId];
            return new PortalWhereUsedParent(link.ParentId, parent.Serial, link.RemovedAt == null,
                link.InstalledAt, link.RemovedAt, parent.Coverage);
        }).ToList();
        var componentCoverage = await CoverageAsync(principal, [componentId], false, true, cancellationToken);
        return new(PortalQueryOutcome.Found, [], componentId, parents,
            componentCoverage.GetValueOrDefault(componentId) ?? []);
    }

    public async Task<PortalCertificatePage?> CertificatesAsync(PortalPrincipal principal, Guid unitId, int take,
        DateTime? beforeIssuedAt, Guid? beforeCertificateId, CancellationToken cancellationToken)
    {
        var windows = await ownership.WindowsAsync(principal, unitId, cancellationToken);
        if (windows.Count == 0) return null;
        take = Math.Clamp(take, 1, 200);
        var viewer = (await partyNames.NamesAsync([principal.PartyRef], cancellationToken)).GetValueOrDefault(principal.PartyRef);
        var answer = new List<PortalCertificateRow>();
        var cursorAt = beforeIssuedAt;
        var cursorId = beforeCertificateId;
        while (answer.Count <= take)
        {
            var query = db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId
                && (row.Status == CertificateStatus.Issued || row.Status == CertificateStatus.Superseded));
            if (cursorAt is { } at && cursorId is { } id)
                query = query.Where(row => row.IssuedAt < at
                    || row.IssuedAt == at && row.CertificateId.CompareTo(id) < 0);
            var batch = await query.OrderByDescending(row => row.IssuedAt).ThenByDescending(row => row.CertificateId)
                .Take(100).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                CertificateOwnerSnapshot? ownerSnapshot = null;
                if (row.DocumentKind == CertificateDocumentKind.WarrantyCertificate)
                {
                    if (row.OwnerSnapshot is null) continue;
                    ownerSnapshot = JsonSerializer.Deserialize<CertificateOwnerSnapshot>(row.OwnerSnapshot,
                        CanonicalJson.Options);
                    if (ownerSnapshot is null || viewer is null
                        || !string.Equals(ownerSnapshot.Code, viewer.Code, StringComparison.Ordinal)) continue;
                }
                answer.Add(new(row.CertificateId, row.DocumentKind, row.FormType, row.Version, row.TrackingNumber,
                    row.Status, row.IssuedAt,
                    row.DocumentKind == CertificateDocumentKind.ReleaseRecord ? null : ownerSnapshot));
                if (answer.Count > take) break;
            }
            var lastBatchRow = batch[^1];
            cursorAt = lastBatchRow.IssuedAt;
            cursorId = lastBatchRow.CertificateId;
            if (batch.Count < 100 || answer.Count > take) break;
        }
        var hasMore = answer.Count > take;
        var rows = answer.Take(take).ToList();
        var last = rows.LastOrDefault();
        return new(rows, hasMore, hasMore ? last?.IssuedAt : null, hasMore ? last?.CertificateId : null);
    }

    private async Task<IReadOnlyList<PortalUnitSummary>> SummariesAsync(PortalPrincipal principal,
        IReadOnlyCollection<Guid> unitIds, bool includeClosedCoverage, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return [];
        var ids = unitIds.Distinct().ToList();
        var accessible = ownership.AccessibleUnitIds(principal);
        var states = await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.UnitId) && accessible.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.ItemId, row.LifecycleStatus, row.ConditionGrade })
            .ToListAsync(cancellationToken);
        var visibleIds = states.Select(row => row.UnitId).ToList();
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && visibleIds.Contains(row.UnitId))
            .OrderBy(row => row.ValidFrom).Select(row => new { row.UnitId, row.IdentifierId, row.Type,
                row.NormalisedValue, row.ValidTo, row.SupersededByIdentifierId }).ToListAsync(cancellationToken);
        var successorIds = identifiers.Where(row => row.SupersededByIdentifierId != null)
            .Select(row => row.SupersededByIdentifierId!.Value).Distinct().ToList();
        var successors = successorIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && successorIds.Contains(row.IdentifierId))
                .ToDictionaryAsync(row => row.IdentifierId, row => row.NormalisedValue, cancellationToken);
        var labels = await itemNames.NamesAsync(states.Select(row => row.ItemId).Distinct().ToList(), cancellationToken);
        var coverage = await CoverageAsync(principal, visibleIds, includeClosedCoverage, false, cancellationToken);
        return states.Select(state =>
        {
            var unitIdentifiers = identifiers.Where(row => row.UnitId == state.UnitId).ToList();
            var serial = unitIdentifiers.Where(row => row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
                .Select(row => row.NormalisedValue).FirstOrDefault()
                ?? unitIdentifiers.Where(row => row.ValidTo == null).Select(row => row.NormalisedValue).FirstOrDefault()
                ?? "Unlabelled unit";
            var lineage = unitIdentifiers.Select(row => new PortalIdentifierLineage(row.NormalisedValue, row.Type,
                row.ValidTo == null, row.SupersededByIdentifierId is { } next ? successors.GetValueOrDefault(next) : null)).ToList();
            var item = labels.GetValueOrDefault(state.ItemId);
            return new PortalUnitSummary(state.UnitId, serial, state.LifecycleStatus,
                item?.Code ?? "Unknown item", item?.Name, state.ConditionGrade,
                coverage.GetValueOrDefault(state.UnitId) ?? [], lineage);
        }).ToList();
    }

    private static async Task<List<InstalledBaseKey>> PageKeysAsync(IQueryable<InstalledBaseKey> source, int take,
        DateTime? afterDeliveryDate, Guid? afterUnitId, CancellationToken cancellationToken)
    {
        if (afterDeliveryDate is { } afterDate && afterUnitId is { } afterId)
            source = source.Where(row => row.DeliveryDate > afterDate
                || row.DeliveryDate == afterDate && row.UnitId.CompareTo(afterId) > 0);
        return await source.OrderBy(row => row.DeliveryDate).ThenBy(row => row.UnitId).Take(take)
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<PortalCoverageExpiry>>> CoverageAsync(PortalPrincipal principal,
        List<Guid> unitIds, bool includeClosed, bool componentScopeOnly,
        CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return [];
        var ids = unitIds.Distinct().ToList();
        var query = db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.OwnerPartyRef == principal.PartyRef
            && (ids.Contains(row.UnitId) || row.ScopeChildUnitId != null && ids.Contains(row.ScopeChildUnitId.Value)));
        if (!includeClosed) query = query.Where(row => row.ClosedAt == null);
        if (componentScopeOnly) query = query.Where(row => row.ScopeChildUnitId != null
            && ids.Contains(row.ScopeChildUnitId.Value));
        var rows = await query.Select(row => new { Key = row.ScopeChildUnitId ?? row.UnitId, row.CoverageFamily,
                row.StartDate, row.EndDate, row.Status, row.CreatedAt })
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        return rows.GroupBy(row => row.Key).ToDictionary(group => group.Key,
            group => (IReadOnlyList<PortalCoverageExpiry>)group.GroupBy(row => row.CoverageFamily)
                .Select(family => family.OrderByDescending(row => row.CreatedAt).First())
                .Select(row => new PortalCoverageExpiry(row.CoverageFamily, row.StartDate, row.EndDate,
                    row.EndDate is { } end && end < today ? "expired" : row.Status))
                .OrderBy(row => row.CoverageFamily, StringComparer.Ordinal).ToList());
    }

    private async Task<IReadOnlyList<Guid>> ResolveUnitIdsAsync(PortalPrincipal principal,
        IReadOnlyList<PortalIdentifierCandidate> candidates, CancellationToken cancellationToken)
    {
        var accessible = ownership.AccessibleUnitIds(principal);
        return await db.Identifiers.AsNoTracking().MatchExact(tenant.TenantId, candidates)
            .Where(row => accessible.Contains(row.UnitId))
            .Select(row => row.UnitId).Distinct().Take(2).ToListAsync(cancellationToken);
    }

    private IdentifierValidation Validate(PortalSerialInput input)
    {
        var types = input.IdentifierType is null ? LookupTypes : [input.IdentifierType];
        var candidates = new List<PortalIdentifierCandidate>();
        var reasons = new List<string>();
        foreach (var type in types)
        {
            IdentifierPolicy policy;
            try { policy = identifierPolicies.For(type); }
            catch (ArgumentException)
            {
                return new([], ["identifier_type_unknown"]);
            }
            var normalised = IdentifierNormaliser.Normalise(input.Value, policy).Value;
            var outcome = IdentifierValidator.Validate(normalised, policy);
            if (outcome.IsValid)
                candidates.Add(new(type, normalised));
            else if (input.IdentifierType is not null)
                reasons.AddRange(outcome.Reasons);
        }
        if (string.IsNullOrWhiteSpace(input.Value)) reasons.Add("empty");
        else if (input.IdentifierType is null && candidates.Count == 0) reasons.Add("invalid_for_all_identifier_types");
        return new(candidates.Distinct().ToList(), reasons.Distinct(StringComparer.Ordinal).ToList());
    }

    private static async Task<List<ServiceEvent>> ServiceEventsAsync<TEvent>(IQueryable<TEvent> source, Guid unitId,
        PortalOwnershipWindow window, int take, DateTime? beforeAt, long? beforeSequence,
        CancellationToken cancellationToken) where TEvent : Data.Entities.UnitEventRow
    {
        var query = source.Where(row => row.UnitId == unitId && row.EventTime >= window.From
            && (window.To == null || row.EventTime < window.To)
            && (row.EventType.StartsWith("rma.") || row.EventType.StartsWith("replacement.")
                || row.EventType.StartsWith("refurbishment.") || row.EventType.StartsWith("certificate.")
                || row.EventType.StartsWith("claim."))
            && row.EventType != RefurbishmentEventType.CostPosted
            && row.EventType != "replacement.non_return_billed");
        if (beforeAt is { } at && beforeSequence is { } sequence)
            query = query.Where(row => row.EventTime < at || row.EventTime == at && row.Seq < sequence);
        return await query.OrderByDescending(row => row.EventTime).ThenByDescending(row => row.Seq).Take(take)
            .Select(row => new ServiceEvent(row.EventId, row.Seq, row.EventType, row.EventTime, row.Disposition))
            .ToListAsync(cancellationToken);
    }

    private sealed record IdentifierValidation(IReadOnlyList<PortalIdentifierCandidate> Candidates,
        IReadOnlyList<string> Reasons);
    private sealed record IdentifierHit(Guid UnitId, string Type, string Value);
    private sealed record ServiceEvent(Guid EventId, long Sequence, string EventType, DateTime At, string? Status);

    private sealed class InstalledBaseKey
    {
        public Guid UnitId { get; init; }
        public DateTime DeliveryDate { get; init; }
        public string? CustomerPoRef { get; init; }
        public string? CustomerItemRef { get; init; }
        public bool IsComponent { get; init; }
    }
}
