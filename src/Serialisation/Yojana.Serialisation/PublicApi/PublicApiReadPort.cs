using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Portal;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.PublicApi;

public sealed class PublicApiReadPort(
    SerialisationDbContext db,
    ITenantContext tenant,
    PortalOwnershipScope ownership,
    UnitQueries units,
    IItemNamePort itemNames,
    DisclosurePolicyService disclosure,
    CertificateQueries certificates) : IPublicApiReadPort
{
    public async Task<PublicApiUnit?> UnitAsync(Guid unitId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        if (!await CanReadUnitAsync(unitId, customerPartyId, cancellationToken)) return null;
        var state = await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId)
            .Select(row => new { row.UnitId, row.ItemId, row.LifecycleStatus, row.ConditionGrade })
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null) return null;
        var labels = await itemNames.NamesAsync([state.ItemId], cancellationToken);
        var label = labels.GetValueOrDefault(state.ItemId);
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId)
            .OrderBy(row => row.ValidFrom).ThenBy(row => row.IdentifierId)
            .Select(row => new PublicApiIdentifier(row.IdentifierId, row.Type, row.NormalisedValue,
                row.ValidTo == null, row.SupersededByIdentifierId))
            .ToListAsync(cancellationToken);
        var delivery = await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId && row.Kind == EpisodeKind.Ownership
                && (customerPartyId == null || row.PartyRef == customerPartyId))
            .OrderByDescending(row => row.ValidFrom).Select(row => (DateTime?)row.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);
        var build = await AsBuiltAsync(unitId, cancellationToken);
        return new(state.UnitId, state.LifecycleStatus, state.ItemId, label?.Code ?? "Unknown item",
            label?.Name, state.ConditionGrade, null, delivery, identifiers, build);
    }

    public async Task<PublicApiPage<PublicApiUnit>> UnitsByIdentifierAsync(string identifier, string? after,
        int limit, Guid? customerPartyId, CancellationToken cancellationToken)
    {
        limit = Limit(limit);
        var afterId = Decode(after);
        var lookup = await units.LookupAsync(identifier, null, cancellationToken);
        var ids = lookup.Hits.Select(row => row.UnitId).Distinct()
            .Where(id => afterId is null || id.CompareTo(afterId.Value) > 0)
            .Order().Take(limit + 1).ToList();
        var answer = new List<PublicApiUnit>(Math.Min(ids.Count, limit));
        foreach (var id in ids)
        {
            if (answer.Count == limit) break;
            if (await UnitAsync(id, customerPartyId, cancellationToken) is { } row) answer.Add(row);
        }
        var hasMore = ids.Count > limit;
        return new(answer, hasMore && answer.Count > 0 ? Encode(answer[^1].Id) : null);
    }

    public async Task<PublicApiUnitCover?> CoverAsync(Guid unitId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        if (!await CanReadUnitAsync(unitId, customerPartyId, cancellationToken)) return null;
        var rows = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId)
            .OrderBy(row => row.CoverageFamily).ThenBy(row => row.EntitlementId)
            .Select(row => new { row.EntitlementId, row.UnitId, row.CoverageFamily, row.Type, row.Status,
                row.StartDate, row.EndDate, row.StartBasis, row.OwnerPartyRef, row.ObligorEntityId, row.Scope,
                row.ScopeChildUnitId })
            .ToListAsync(cancellationToken);
        var childUnitIds = rows.Select(row => row.ScopeChildUnitId).OfType<Guid>().ToList();
        var itemIds = await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.UnitId == unitId || childUnitIds.Contains(row.UnitId)))
            .Select(row => new { row.UnitId, row.ItemId }).ToListAsync(cancellationToken);
        var names = await itemNames.NamesAsync(itemIds.Select(row => row.ItemId).Distinct().ToList(),
            cancellationToken);
        var currentItems = itemIds.ToDictionary(row => row.UnitId, row => row.ItemId);
        var cover = rows.Select(row =>
        {
            var coveredId = row.ScopeChildUnitId ?? row.UnitId;
            var itemId = currentItems.GetValueOrDefault(coveredId);
            var name = names.GetValueOrDefault(itemId);
            return new PublicApiCover(row.EntitlementId, row.UnitId, row.CoverageFamily, row.Type, row.Status,
                row.StartDate, row.EndDate, row.StartBasis, row.OwnerPartyRef, row.ObligorEntityId,
                [new PublicApiCoveredUnit(coveredId, row.Scope, itemId == Guid.Empty ? null : itemId,
                    name?.Code, name?.Name)]);
        }).ToList();
        return new(unitId, cover);
    }

    public async Task<PublicApiClaim?> ClaimAsync(Guid claimId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        var row = await db.Claims.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.ClaimId == claimId)
            .Select(item => new { item.ClaimId, item.ClaimNumber, item.UnitId, item.ClaimDate, item.Status,
                item.Remedy, item.CoverageSource, item.ServiceCountry, item.Currency, item.ClaimantPartyRef,
                item.SettlementPartyRef, item.ServiceEntityId, item.ObligorEntityId, item.ReportedCauseCode,
                item.ReportedDescription }).SingleOrDefaultAsync(cancellationToken);
        if (row is null || !await CanReadUnitAsync(row.UnitId, customerPartyId, cancellationToken)) return null;
        var lines = await db.ClaimLines.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.ClaimId == claimId).OrderBy(item => item.Sequence)
            .Select(item => new PublicApiClaimLine(item.ClaimLineId, item.UnitId, item.Kind,
                item.CoverageFamily, item.Description, item.RequestedAmount, item.ApprovedAmount,
                item.CoverageSource, item.DenialReason)).ToListAsync(cancellationToken);
        var evidence = await db.ClaimEvidence.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.ClaimId == claimId).OrderBy(item => item.RecordedAt).ThenBy(item => item.EvidenceId)
            .Select(item => new PublicApiClaimEvidence(item.EvidenceId, item.Kind, item.Reference,
                item.Description, item.RecordedAt, item.AttachmentFileName, item.AttachmentContentType))
            .ToListAsync(cancellationToken);
        return new(row.ClaimId, row.ClaimNumber, row.UnitId, row.ClaimDate, row.Status, row.Remedy,
            row.CoverageSource, row.ServiceCountry, row.Currency, row.ClaimantPartyRef,
            row.SettlementPartyRef, row.ServiceEntityId, row.ObligorEntityId, row.ReportedCauseCode,
            row.ReportedDescription, lines, evidence);
    }

    public Task<PublicApiPage<PublicApiClaim>> ClaimsAsync(Guid unitId, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken) => PageAsync(
        db.Claims.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId)
            .Select(row => row.ClaimId), after, limit, customerPartyId, ClaimAsync, cancellationToken);

    public async Task<PublicApiRma?> RmaAsync(Guid rmaId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        var row = await db.Rmas.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.RmaId == rmaId)
            .Select(item => new { item.RmaId, item.RmaNumber, item.Stage, item.RequestDate,
                item.ReturningPartyRef, item.SettlementPartyRef, item.ServiceEntityId, item.ObligorEntityId,
                item.ServiceCountry, item.Currency }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var lines = await db.RmaLines.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.RmaId == rmaId).OrderBy(item => item.Sequence)
            .Select(item => new PublicApiRmaLine(item.RmaLineId, item.RequestedUnitId,
                item.CoverageFamily, item.ClaimLineKind, item.Description, item.RequestedAmount,
                item.ApprovedAmount, item.CoverageSource, item.DenialReason, item.Disposition))
            .ToListAsync(cancellationToken);
        if (lines.Count == 0 || !await CanReadUnitAsync(lines[0].UnitId, customerPartyId, cancellationToken))
            return null;
        return new(row.RmaId, row.RmaNumber, row.Stage, row.RequestDate, row.ReturningPartyRef,
            row.SettlementPartyRef, row.ServiceEntityId, row.ObligorEntityId, row.ServiceCountry,
            row.Currency, lines);
    }

    public Task<PublicApiPage<PublicApiRma>> RmasAsync(Guid unitId, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken) => PageAsync(
        db.RmaLines.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.RequestedUnitId == unitId).Select(row => row.RmaId).Distinct(), after, limit,
        customerPartyId, RmaAsync, cancellationToken);

    public async Task<PublicApiReplacement?> ReplacementAsync(Guid replacementId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        var row = await db.ReplacementLinks.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.ReplacementLinkId == replacementId)
            .Select(item => new PublicApiReplacement(item.ReplacementLinkId, item.OriginalUnitId,
                item.ReplacementUnitId, item.State, item.ReplacementReason, item.CreatedAt, item.ShippedAt,
                item.ReturnDue, item.CounterpartyPartyRef)).SingleOrDefaultAsync(cancellationToken);
        if (row is null || !await CanReadUnitAsync(row.OriginalUnitId, customerPartyId, cancellationToken))
            return null;
        return row;
    }

    public Task<PublicApiPage<PublicApiReplacement>> ReplacementsAsync(Guid unitId, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken) => PageAsync(
        db.ReplacementLinks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.OriginalUnitId == unitId || row.ReplacementUnitId == unitId))
            .Select(row => row.ReplacementLinkId), after, limit, customerPartyId, ReplacementAsync,
        cancellationToken);

    public async Task<PublicApiCertificate?> CertificateAsync(Guid certificateId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        var row = await db.CertificateInstances.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.CertificateId == certificateId)
            .Select(item => new PublicApiCertificate(item.CertificateId, item.UnitId, item.DocumentKind,
                item.FormType, item.Version, item.TrackingNumber, item.Status, item.IssuedAt,
                item.SupersedesCertificateId)).SingleOrDefaultAsync(cancellationToken);
        return row is not null && await CanReadUnitAsync(row.UnitId, customerPartyId, cancellationToken)
            ? row : null;
    }

    public async Task<PublicApiDocument?> CertificatePdfAsync(Guid certificateId, Guid? customerPartyId,
        CancellationToken cancellationToken)
    {
        var record = await CertificateAsync(certificateId, customerPartyId, cancellationToken);
        if (record is null) return null;
        var rendered = await certificates.RenderAsync(certificateId, cancellationToken);
        return rendered is null ? null : new(rendered.Bytes, rendered.MediaType,
            $"certificate-{record.TrackingNumber}.pdf");
    }

    public async Task<IReadOnlySet<Guid>> VisibleUnitIdsAsync(IReadOnlyList<Guid> unitIds,
        Guid? customerPartyId, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return new HashSet<Guid>();
        var source = customerPartyId is { } party
            ? ownership.AccessibleUnitIds(new PortalPrincipal(tenant.TenantId, party))
            : db.Units.AsNoTracking().Where(row => row.TenantId == tenant.TenantId).Select(row => row.UnitId);
        return (await source.Where(unitId => unitIds.Contains(unitId)).ToListAsync(cancellationToken)).ToHashSet();
    }

    public async Task<PublicApiDisclosurePolicy> DisclosureAsync(string tier,
        CancellationToken cancellationToken)
    {
        if (!PublicApiDisclosureTier.All.Contains(tier))
            throw new CommandRefusedException("api_disclosure_tier_unknown", "The API disclosure tier is unknown.");
        var policy = await disclosure.ReadAsync(cancellationToken);
        var rank = tier == PublicApiDisclosureTier.BackOffice ? 2 : 1;
        var fields = policy.Fields.Where(row => row.Enabled && Rank(row.Tier) <= rank)
            .Select(row => row.Field).ToHashSet(StringComparer.Ordinal);
        return new(fields);
    }

    private async Task<PublicApiAsBuilt?> AsBuiltAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var build = await db.BuildRecords.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId).OrderByDescending(row => row.Version)
            .Select(row => new { row.Version, row.ReleasedAt }).FirstOrDefaultAsync(cancellationToken);
        if (build is null) return null;
        var rows = await db.AssemblyLinks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ParentUnitId == unitId && row.InstalledAt <= build.ReleasedAt
                && (row.RemovedAt == null || row.RemovedAt > build.ReleasedAt))
            .Select(row => new { row.ChildUnitId, row.ChildItemId, row.Quantity, row.Relation, row.Slot })
            .ToListAsync(cancellationToken);
        var unitIds = rows.Where(row => row.ChildUnitId != null).Select(row => row.ChildUnitId!.Value).ToList();
        var childItems = unitIds.Count == 0 ? new Dictionary<Guid, Guid>()
            : await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && unitIds.Contains(row.UnitId)).ToDictionaryAsync(row => row.UnitId, row => row.ItemId,
                cancellationToken);
        var ids = rows.Select(row => row.ChildItemId)
            .Concat(childItems.Values.Select(item => (Guid?)item)).OfType<Guid>().Distinct().ToList();
        var names = await itemNames.NamesAsync(ids, cancellationToken);
        var parts = rows.Select(row =>
        {
            var itemId = row.ChildItemId ?? (row.ChildUnitId is { } child
                ? childItems.GetValueOrDefault(child) : Guid.Empty);
            var name = names.GetValueOrDefault(itemId);
            return new PublicApiAsBuiltPart(row.ChildUnitId, itemId == Guid.Empty ? null : itemId,
                name?.Code, name?.Name, row.Quantity, row.Relation, row.Slot);
        }).ToList();
        return new(build.Version, build.ReleasedAt, parts);
    }

    private async Task<bool> CanReadUnitAsync(Guid unitId, Guid? customerPartyId,
        CancellationToken cancellationToken) => customerPartyId is null
        ? await db.Units.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.UnitId == unitId, cancellationToken)
        : await ownership.CanReadAsync(new PortalPrincipal(tenant.TenantId, customerPartyId.Value), unitId,
            cancellationToken);

    private static async Task<PublicApiPage<T>> PageAsync<T>(IQueryable<Guid> source, string? after, int limit,
        Guid? customerPartyId, Func<Guid, Guid?, CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken) where T : class
    {
        limit = Limit(limit);
        var cursor = Decode(after);
        if (cursor is { } cursorId) source = source.Where(value => value.CompareTo(cursorId) > 0);
        var ids = await source.Order().Take(limit + 1).ToListAsync(cancellationToken);
        var rows = new List<T>(Math.Min(limit, ids.Count));
        foreach (var resourceId in ids.Take(limit))
            if (await read(resourceId, customerPartyId, cancellationToken) is { } row) rows.Add(row);
        return new(rows, ids.Count > limit && ids.Count > 0 ? Encode(ids[Math.Min(limit - 1, ids.Count - 1)]) : null);
    }

    private static int Limit(int value) => Math.Clamp(value, 1, 200);

    private static string Encode(Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString("N")))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Guid? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            return Guid.TryParseExact(Encoding.UTF8.GetString(Convert.FromBase64String(value)), "N", out var id)
                ? id : null;
        }
        catch (FormatException) { return null; }
    }

    private static int Rank(string tier) => tier switch
    {
        DisclosureTier.Public => 0,
        DisclosureTier.Owner => 1,
        _ => 2,
    };
}
