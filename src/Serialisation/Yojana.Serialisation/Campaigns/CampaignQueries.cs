using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Campaigns;

public sealed class CampaignQueries(SerialisationDbContext db, CampaignSelectionFacts facts, ITenantContext tenant)
{
    private static readonly string[] FunnelOrder =
    [
        CampaignCriterionKind.IdentifierRange,
        CampaignCriterionKind.Child,
        CampaignCriterionKind.Firmware,
        CampaignCriterionKind.Lot,
    ];

    public async Task<Page<CampaignListRow>> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var query = db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Code).Skip(skip).Take(take)
            .Select(c => new CampaignListRow(c.CampaignId, c.Code, c.Name, c.CoverageSource, c.Status,
                db.CampaignUnits.Count(u => u.TenantId == tenant.TenantId && u.CampaignId == c.CampaignId),
                c.SelectedAt, c.ReserveTag, c.ReasonCode, c.RunsUntil))
            .ToListAsync(cancellationToken);
        return new(rows, total, skip, take);
    }

    public async Task<CampaignRow?> DetailAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId, cancellationToken);
        if (campaign is null) return null;
        return CampaignHandler.ToRow(campaign, await CriteriaAsync(campaignId, cancellationToken));
    }

    public async Task<IReadOnlyList<CampaignFunnelStep>?> FunnelAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        if (!await db.Campaigns.AsNoTracking().AnyAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId, cancellationToken)) return null;
        var criteria = await CriteriaAsync(campaignId, cancellationToken);
        var included = new List<CampaignCriterion>();
        var result = new List<CampaignFunnelStep>();
        foreach (var kind in FunnelOrder)
        {
            var group = criteria.Where(c => c.Kind == kind).ToList();
            if (group.Count == 0) continue;
            included.AddRange(group);
            result.Add(new(kind, (await facts.MatchingUnitIdsAsync(included, cancellationToken)).Count));
        }
        return result;
    }

    public async Task<IReadOnlyList<CampaignMatchEvidence>?> MatchAsync(Guid campaignId, Guid unitId, CancellationToken cancellationToken)
    {
        var criteria = await CriteriaAsync(campaignId, cancellationToken);
        if (criteria.Count == 0) return null;
        if (!(await facts.MatchingUnitIdsAsync(criteria, cancellationToken)).Contains(unitId)) return [];
        var row = (await facts.CurrentMatchesAsync([unitId], criteria, cancellationToken)).SingleOrDefault();
        if (row is null) return [];
        var evidence = new List<CampaignMatchEvidence>();
        foreach (var kind in FunnelOrder)
        {
            var group = criteria.Where(c => c.Kind == kind).ToList();
            if (group.Count == 0) continue;
            switch (kind)
            {
                case CampaignCriterionKind.IdentifierRange:
                    evidence.Add(new(kind, "Range", string.Join(" or ", group.Select(c => $"{c.Scheme} in {c.RangeStart} … {c.RangeEnd}"))));
                    break;
                case CampaignCriterionKind.Child:
                    evidence.Add(new(kind, "Child", row.MatchedChildSerial is null ? "Non-serialised child matched" : row.MatchedChildSerial));
                    break;
                case CampaignCriterionKind.Firmware:
                    evidence.Add(new(kind, row.FirmwareKey ?? "Firmware", row.FirmwareValue is null
                        ? "Matched at the criterion date"
                        : $"{row.FirmwareValue}{(row.FirmwareSince is { } since ? $" since {since:yyyy-MM-dd}" : "")}"));
                    break;
                case CampaignCriterionKind.Lot:
                    evidence.Add(new(kind, "Lot", "Unit lot matched"));
                    break;
            }
        }
        return evidence;
    }

    public async Task<Page<CampaignUnitDetailRow>?> UnitsAsync(Guid campaignId, string? serial, Guid? owner,
        string? custody, bool? ownerDiffersInvoice, string? repair, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId, cancellationToken);
        if (campaign is null) return null;

        var query = UnitFacts(campaign);
        if (!string.IsNullOrWhiteSpace(serial)) query = query.Where(row => row.Serial != null && row.Serial.Contains(serial));
        if (owner is { } ownerRef) query = query.Where(row => row.OwnerPartyRef == ownerRef);
        if (!string.IsNullOrWhiteSpace(custody)) query = query.Where(row => row.CustodyKind == custody);
        if (ownerDiffersInvoice is { } differs) query = query.Where(row => (row.InvoicePartyRef != null && row.InvoicePartyRef != row.OwnerPartyRef) == differs);
        if (repair == "with_claim") query = query.Where(row => row.ClaimId != null);
        if (repair == "without_claim") query = query.Where(row => row.ClaimId == null);
        if (repair == "repaired") query = query.Where(row => row.ClaimStatus == ClaimStatus.Settled || row.ClaimStatus == ClaimStatus.Closed);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.Serial).ThenBy(row => row.UnitId).Skip(skip).Take(take)
            .Select(row => new CampaignUnitDetailRow(row.UnitId, row.Serial, row.OwnerPartyRef, row.OwnerPartyType,
                row.OwnerSince, row.CustodianPartyRef, row.CustodianPartyType, row.CustodyKind, row.CustodyAtSelection, row.CustodySince,
                row.InvoicePartyRef, row.SelectedAt, row.SelectedBy, row.ClaimId, row.ClaimNumber, row.ClaimStatus))
            .ToListAsync(cancellationToken);
        return new(rows, total, skip, take);
    }

    public async Task<CampaignUnitsSummary?> UnitsSummaryAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId, cancellationToken);
        if (campaign is null) return null;
        var rows = UnitFacts(campaign);
        var units = await rows.CountAsync(cancellationToken);
        var owners = await rows.Where(r => r.OwnerPartyRef != null).Select(r => r.OwnerPartyRef).Distinct().CountAsync(cancellationToken);
        var custodians = await rows.Where(r => r.CustodianPartyRef != null && r.CustodianPartyRef != r.OwnerPartyRef)
            .Select(r => r.CustodianPartyRef).Distinct().CountAsync(cancellationToken);
        var differs = await rows.CountAsync(r => r.InvoicePartyRef != null && r.InvoicePartyRef != r.OwnerPartyRef, cancellationToken);
        var repaired = await rows.CountAsync(r => r.ClaimStatus == ClaimStatus.Settled || r.ClaimStatus == ClaimStatus.Closed, cancellationToken);
        var inOurHands = await rows.CountAsync(r => r.CustodyKind == LocationKind.Warehouse || r.CustodyKind == LocationKind.Bin
            || r.CustodyKind == LocationKind.At3pl || r.CustodyKind == LocationKind.InRepair, cancellationToken);
        var added = campaign.SelectedAt is null ? 0 : await rows.CountAsync(r => r.SelectedAt > campaign.SelectedAt, cancellationToken);
        return new(units, campaign.SelectedAt, added, owners + custodians, owners, custodians, differs, repaired, inOurHands);
    }

    public async IAsyncEnumerable<CampaignUnitDetailRow> ExportUnitsAsync(Guid campaignId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int batchSize = 200;
        var skip = 0;
        while (await UnitsAsync(campaignId, null, null, null, null, null, skip, batchSize, cancellationToken) is { } page)
        {
            foreach (var row in page.Items) yield return row;
            skip += page.Items.Count;
            if (page.Items.Count == 0 || skip >= page.Total) yield break;
        }
    }

    public Task<List<Guid>> RecipientPartyRefsAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var units = db.CampaignUnits.AsNoTracking().Where(u => u.TenantId == tenant.TenantId && u.CampaignId == campaignId);
        var owners = units.Where(u => u.OwnerPartyRef != null).Select(u => u.OwnerPartyRef!.Value);
        var custodians = units.Where(u => u.CustodianPartyRef != null).Select(u => u.CustodianPartyRef!.Value);
        return owners.Concat(custodians).Distinct().ToListAsync(cancellationToken);
    }

    private IQueryable<UnitFact> UnitFacts(Campaign campaign)
    {
        return from selected in db.CampaignUnits.AsNoTracking()
               where selected.TenantId == tenant.TenantId && selected.CampaignId == campaign.CampaignId
               let serial = db.Identifiers.Where(i => i.TenantId == tenant.TenantId && i.UnitId == selected.UnitId
                   && i.ValidTo == null && i.Type == IdentifierType.InternalSerial).Select(i => i.NormalisedValue).FirstOrDefault()
               let ownerEpisode = db.Episodes.Where(e => e.TenantId == tenant.TenantId && e.UnitId == selected.UnitId
                   && e.Kind == EpisodeKind.Ownership && e.PartyRef == selected.OwnerPartyRef && e.ValidFrom <= selected.SelectedAt
                   && (e.ValidTo == null || e.ValidTo > selected.SelectedAt)).OrderByDescending(e => e.ValidFrom).FirstOrDefault()
               let custodyEpisode = db.Episodes.Where(e => e.TenantId == tenant.TenantId && e.UnitId == selected.UnitId
                   && e.Kind == EpisodeKind.Custody && e.PartyRef == selected.CustodianPartyRef && e.ValidFrom <= selected.SelectedAt
                   && (e.ValidTo == null || e.ValidTo > selected.SelectedAt)).OrderByDescending(e => e.ValidFrom).FirstOrDefault()
               let invoiceParty = db.Episodes.Where(e => e.TenantId == tenant.TenantId && e.UnitId == selected.UnitId
                   && e.Kind == EpisodeKind.Ownership && e.Reason == EpisodeReason.Sale && e.OpeningDocumentId != null)
                   .OrderBy(e => e.ValidFrom).Select(e => e.PartyRef).FirstOrDefault()
               let currentCustody = db.Current.Where(c => c.TenantId == tenant.TenantId && c.UnitId == selected.UnitId)
                   .Select(c => c.LocationKind).FirstOrDefault()
               let claim = db.Claims.Where(c => c.TenantId == tenant.TenantId && c.UnitId == selected.UnitId
                   && c.CoverageSource == campaign.CoverageSource && c.SubmittedAt >= selected.SelectedAt)
                   .OrderByDescending(c => c.SubmittedAt).FirstOrDefault()
               // Member initialisation, not a constructor: EF translates later filters on these members into SQL.
               select new UnitFact
               {
                   UnitId = selected.UnitId, Serial = serial, OwnerPartyRef = selected.OwnerPartyRef, OwnerPartyType = selected.OwnerPartyType,
                   OwnerSince = ownerEpisode == null ? null : ownerEpisode.ValidFrom, CustodianPartyRef = selected.CustodianPartyRef,
                   CustodianPartyType = selected.CustodianPartyType, CustodyKind = currentCustody,
                   CustodyAtSelection = custodyEpisode == null ? null : custodyEpisode.LocationKind,
                   CustodySince = custodyEpisode == null ? null : custodyEpisode.ValidFrom, InvoicePartyRef = invoiceParty,
                   SelectedAt = selected.SelectedAt, SelectedBy = selected.SelectedBy, ClaimId = claim == null ? null : claim.ClaimId,
                   ClaimNumber = claim == null ? null : claim.ClaimNumber, ClaimStatus = claim == null ? null : claim.Status,
               };
    }

    private Task<List<CampaignCriterion>> CriteriaAsync(Guid campaignId, CancellationToken cancellationToken) =>
        db.CampaignCriteria.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId)
            .OrderBy(c => c.Kind).ThenBy(c => c.CriterionId).ToListAsync(cancellationToken);

    private sealed class UnitFact
    {
        public Guid UnitId { get; init; }
        public string? Serial { get; init; }
        public Guid? OwnerPartyRef { get; init; }
        public string? OwnerPartyType { get; init; }
        public DateTime? OwnerSince { get; init; }
        public Guid? CustodianPartyRef { get; init; }
        public string? CustodianPartyType { get; init; }
        public string? CustodyKind { get; init; }
        public string? CustodyAtSelection { get; init; }
        public DateTime? CustodySince { get; init; }
        public Guid? InvoicePartyRef { get; init; }
        public DateTime SelectedAt { get; init; }
        public string SelectedBy { get; init; } = "";
        public Guid? ClaimId { get; init; }
        public string? ClaimNumber { get; init; }
        public string? ClaimStatus { get; init; }
    }
}
