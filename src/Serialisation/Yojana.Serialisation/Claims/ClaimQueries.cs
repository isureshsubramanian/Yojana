using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

public sealed class ClaimQueries(SerialisationDbContext db, ICampaignCoverPort campaigns, ITenantContext tenant) : IWarrantyClaimsPort
{
    private static readonly string[] Settled = [ClaimStatus.Settled, ClaimStatus.Closed];
    private static readonly string[] Decided = [ClaimStatus.Approved, ClaimStatus.PartiallyApproved, ClaimStatus.Denied, ClaimStatus.Settled, ClaimStatus.Closed];

    /// <summary>Upper bounds (inclusive) of the ageing buckets in days from submission; the last bucket is open-ended.</summary>
    public static readonly int[] AgeBucketBounds = [1, 5, 10, 20];

    private IQueryable<Claim> Filtered(ClaimListFilter filter)
    {
        var tenantId = tenant.TenantId;
        var source = db.Claims.AsNoTracking().Where(c => c.TenantId == tenantId);
        source = filter.Status switch
        {
            null or "" or "open" => source.Where(c => !Settled.Contains(c.Status)),
            "all" => source,
            var status => source.Where(c => c.Status == status),
        };
        if (!string.IsNullOrWhiteSpace(filter.CoverageSource)) source = source.Where(c => c.CoverageSource == filter.CoverageSource);
        if (!string.IsNullOrWhiteSpace(filter.ServiceCountry)) source = source.Where(c => c.ServiceCountry == filter.ServiceCountry);
        if (filter.CampaignId is { } campaignId) source = source.Where(c => c.CampaignId == campaignId);
        if (filter.Recovery == "raised") source = source.Where(c => c.RecoveryRaised);
        else if (filter.Recovery == "none") source = source.Where(c => !c.RecoveryRaised);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            // The caller resolves the text to units through identifier lookup; here it also matches a claim number.
            var number = filter.Query.Trim().ToUpperInvariant();
            var units = filter.UnitIds ?? [];
            source = source.Where(c => c.ClaimNumber == number || units.Contains(c.UnitId));
        }

        return source;
    }

    public async Task<ClaimPage> ListAsync(ClaimListFilter filter, DateOnly asOf, int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var source = Filtered(filter);
        var total = await source.CountAsync(cancellationToken);
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var claims = await source.OrderBy(c => c.ListOrderBucket).ThenBy(c => c.SubmittedAt).ThenBy(c => c.ClaimId)
            .Skip(skip).Take(take).ToListAsync(cancellationToken);
        var claimIds = claims.Select(claim => claim.ClaimId).ToList();
        var lineTotals = claimIds.Count == 0
            ? []
            : await db.ClaimLines.AsNoTracking().Where(line => line.TenantId == tenantId && claimIds.Contains(line.ClaimId))
                .GroupBy(line => line.ClaimId)
                .Select(group => new ClaimLineTotals
                {
                    ClaimId = group.Key,
                    Requested = group.Sum(line => line.RequestedAmount),
                    Approved = group.Sum(line => line.ApprovedAmount),
                    Lines = group.Count(),
                }).ToListAsync(cancellationToken);
        var totalsByClaim = lineTotals.ToDictionary(row => row.ClaimId);
        var evidence = claimIds.Count == 0
            ? []
            : await db.ClaimEvidence.AsNoTracking().Where(row => row.TenantId == tenantId && claimIds.Contains(row.ClaimId))
                .OrderBy(row => row.ClaimId).ThenByDescending(row => row.RecordedAt).ThenByDescending(row => row.EvidenceId)
                .Select(row => new { row.ClaimId, row.Kind }).ToListAsync(cancellationToken);
        var lastEvidenceByClaim = evidence.GroupBy(row => row.ClaimId).ToDictionary(group => group.Key, group => group.First().Kind);
        var unitIds = claims.Select(claim => claim.UnitId).Distinct().ToList();
        var itemIds = unitIds.Count == 0 ? [] : await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var serials = await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken);
        var models = new List<ClaimListRow>(claims.Count);
        foreach (var claim in claims)
        {
            totalsByClaim.TryGetValue(claim.ClaimId, out var totals);
            lastEvidenceByClaim.TryGetValue(claim.ClaimId, out var lastEvidenceKind);
            var campaign = claim.CampaignId is { } campaignId
                ? await campaigns.FindAsync(campaignId, claim.UnitId, claim.ClaimDate, cancellationToken)
                : null;
            models.Add(new ClaimListRow(claim.ClaimId, claim.ClaimNumber, claim.UnitId, serials.GetValueOrDefault(claim.UnitId), itemIds.GetValueOrDefault(claim.UnitId),
                claim.ClaimantPartyRef, claim.SettlementPartyRef, claim.Status, claim.CoverageSource, claim.Remedy, claim.ServiceCountry, claim.Currency,
                claim.ClaimDate, claim.SubmittedAt, totals?.Requested ?? 0, totals?.Approved ?? 0, AgeDays(claim.SubmittedAt, asOf), claim.DenialReason, claim.RecoveryRaised,
                lastEvidenceKind == ClaimEvidenceKind.EvidenceRequested, totals?.Lines ?? 0, Decided.Contains(claim.Status) ? totals?.Lines ?? 0 : 0,
                CampaignId: claim.CampaignId, CampaignCode: campaign?.Code, CampaignName: campaign?.Name,
                ReserveTag: claim.ReserveTag, CampaignCoverReason: claim.CampaignCoverReason));
        }
        return new(models, total, skip, take);
    }

    public async Task<ClaimSummary> SummaryAsync(DateOnly asOf, int targetWorkingDays, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var claims = db.Claims.AsNoTracking().Where(c => c.TenantId == tenantId);
        var open = await claims.Where(c => !Settled.Contains(c.Status))
            .Select(c => new
            {
                c.ClaimId, c.ClaimNumber, c.Status, c.SubmittedAt,
                LastEvidenceKind = db.ClaimEvidence.Where(e => e.TenantId == tenantId && e.ClaimId == c.ClaimId).OrderByDescending(e => e.RecordedAt).Select(e => e.Kind).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var monthStart = new DateTime(asOf.Year, asOf.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);
        var decided = await claims.Where(c => c.AdjudicatedAt >= monthStart && c.AdjudicatedAt < monthEnd)
            .Select(c => new
            {
                c.Status, c.Currency, c.DenialReason,
                Approved = db.ClaimLines.Where(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId).Sum(l => l.ApprovedAmount),
            })
            .ToListAsync(cancellationToken);
        var recoveries = await db.RecoveryClaims.AsNoTracking().Where(r => r.TenantId == tenantId && r.Status == RecoveryClaimStatus.Raised)
            .GroupBy(r => r.Currency).Select(g => new { Currency = g.Key, Amount = g.Sum(r => r.RequestedAmount), Count = g.Count() }).ToListAsync(cancellationToken);
        var vendors = await db.RecoveryClaims.AsNoTracking().Where(r => r.TenantId == tenantId && r.Status == RecoveryClaimStatus.Raised)
            .Select(r => r.VendorPartyRef).Distinct().CountAsync(cancellationToken);
        var sources = await claims.Select(c => c.CoverageSource).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var countries = await claims.Select(c => c.ServiceCountry).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var all = await claims.LongCountAsync(cancellationToken);

        var buckets = new int[AgeBucketBounds.Length + 1];
        foreach (var claim in open) buckets[Bucket(AgeDays(claim.SubmittedAt, asOf))]++;
        var oldest = open.OrderBy(c => c.SubmittedAt).ThenBy(c => c.ClaimId).FirstOrDefault();
        var approvedThisMonth = decided.Where(d => d.Status is ClaimStatus.Approved or ClaimStatus.PartiallyApproved or ClaimStatus.Settled or ClaimStatus.Closed && d.Approved > 0).ToList();
        var deniedThisMonth = decided.Where(d => d.Status == ClaimStatus.Denied || (d.Approved == 0 && d.DenialReason is not null)).ToList();
        return new ClaimSummary(asOf, open.Count,
            open.Count(c => c.Status == ClaimStatus.Submitted), open.Count(c => c.Status == ClaimStatus.Adjudicating),
            open.Count(c => c.Status is ClaimStatus.Approved or ClaimStatus.PartiallyApproved), open.Count(c => c.Status == ClaimStatus.Denied),
            approvedThisMonth.GroupBy(d => d.Currency).Select(g => new MoneyTotal(g.Key, g.Sum(d => d.Approved))).OrderByDescending(m => m.Amount).ToList(),
            approvedThisMonth.Count, approvedThisMonth.Count(d => d.Status == ClaimStatus.PartiallyApproved || d.DenialReason == "partially_uncovered"),
            deniedThisMonth.Count,
            deniedThisMonth.GroupBy(d => d.DenialReason ?? "no_applicable_coverage").Select(g => new ReasonCount(g.Key, g.Count())).OrderByDescending(r => r.Count).ToList(),
            recoveries.Select(r => new MoneyTotal(r.Currency, r.Amount)).OrderByDescending(m => m.Amount).ToList(), recoveries.Sum(r => r.Count), vendors,
            oldest?.ClaimNumber, oldest?.ClaimId, oldest is null ? null : AgeDays(oldest.SubmittedAt, asOf), oldest?.Status,
            oldest?.LastEvidenceKind == ClaimEvidenceKind.EvidenceRequested, buckets, targetWorkingDays, sources, countries, all);
    }

    public async Task<ClaimDetail?> DetailAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClaimId == claimId, cancellationToken);
        if (claim is null) return null;
        var lines = await db.ClaimLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.ClaimId == claimId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var evidence = await db.ClaimEvidence.AsNoTracking().Where(e => e.TenantId == tenantId && e.ClaimId == claimId).OrderBy(e => e.RecordedAt).ThenBy(e => e.EvidenceId).ToListAsync(cancellationToken);
        var unitIds = lines.Select(l => l.UnitId).Append(claim.UnitId).Distinct().ToList();
        var serials = await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken);
        var items = await db.Current.AsNoTracking().Where(u => u.TenantId == tenantId && unitIds.Contains(u.UnitId)).ToDictionaryAsync(u => u.UnitId, u => u.ItemId, cancellationToken);
        var recoveries = await db.RecoveryClaims.AsNoTracking().Where(r => r.TenantId == tenantId && r.CustomerClaimId == claimId).OrderBy(r => r.RecoveryNumber)
            .Select(r => new ClaimRecoveryRef(r.RecoveryClaimId, r.RecoveryNumber, r.VendorPartyRef, r.Status)).ToListAsync(cancellationToken);
        var campaign = claim.CampaignId is { } campaignId
            ? await campaigns.FindAsync(campaignId, claim.UnitId, claim.ClaimDate, cancellationToken)
            : null;
        return new ClaimDetail(claim.ClaimId, claim.UnitId, claim.Status, claim.RecoveryRaised, claim.ClaimDate, claim.ClaimantPartyRef, claim.SettlementPartyRef,
            claim.ServiceEntityId, claim.ObligorEntityId, claim.ServiceCountry, claim.Currency, claim.CoverageSource, claim.Remedy, claim.DenialReason, claim.ChainLength,
            JsonSerializer.Deserialize<List<Guid>>(claim.EntitlementIds, LedgerJson.Options) ?? [],
            lines.Select(Decision).ToList(), evidence.Select(e => new ClaimEvidenceRow(e.EvidenceId, e.Kind, e.Reference, e.Description, e.RecordedAt, e.ActorRef)).ToList(),
            claim.AdjudicatedBy, claim.ReviewerRole, claim.SuccessorItemId, claim.ClaimNumber, serials.GetValueOrDefault(claim.UnitId),
            items.TryGetValue(claim.UnitId, out var itemId) ? itemId : null, claim.SubmittedAt, claim.SubmittedBy, claim.AdjudicatedAt,
            lines.Select(l => Row(l, serials, items)).ToList(), recoveries, CampaignId: claim.CampaignId, CampaignCode: campaign?.Code,
            CampaignName: campaign?.Name, ReserveTag: claim.ReserveTag, CampaignCoverReason: claim.CampaignCoverReason,
            Version: DocumentVersion.Encode(claim.RowVersion), ReportedCauseCode: claim.ReportedCauseCode,
            ReportedDescription: claim.ReportedDescription, OccurredDate: claim.OccurredDate);
    }

    internal static ClaimLineRow Row(ClaimLine line, IReadOnlyDictionary<Guid, string> serials, IReadOnlyDictionary<Guid, Guid> items) =>
        new(line.ClaimLineId, line.Sequence, line.UnitId, serials.GetValueOrDefault(line.UnitId), items.TryGetValue(line.UnitId, out var item) ? item : null,
            line.Kind, line.CoverageFamily, line.Description, line.RequestedAmount, line.CoveragePct, line.ApprovedAmount, line.CoverageSource, line.DenialReason,
            JsonSerializer.Deserialize<Dictionary<string, decimal>>(line.Measurements, LedgerJson.Options) ?? []);

    public static int AgeDays(DateTime submittedAt, DateOnly asOf) => Math.Max(0, asOf.DayNumber - DateOnly.FromDateTime(submittedAt).DayNumber);

    public static int Bucket(int ageDays)
    {
        for (var i = 0; i < AgeBucketBounds.Length; i++) if (ageDays <= AgeBucketBounds[i]) return i;
        return AgeBucketBounds.Length;
    }

    public async Task<List<RecoveryClaimRow>> RecoveryListAsync(string? status, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = db.RecoveryClaims.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(r => r.Status == status);
        var facts = await rows.OrderBy(r => r.Status).ThenBy(r => r.RaisedAt).Select(r => new
        {
            Recovery = r,
            CustomerClaimNumber = db.Claims.Where(c => c.TenantId == tenantId && c.ClaimId == r.CustomerClaimId).Select(c => c.ClaimNumber).First(),
        }).ToListAsync(cancellationToken);
        return facts.Select(x => new RecoveryClaimRow(x.Recovery.RecoveryClaimId, x.Recovery.CustomerClaimId, x.Recovery.ClaimLineId,
            x.Recovery.InboundEntitlementId, x.Recovery.ChildUnitId, x.Recovery.VendorPartyRef, x.Recovery.RequestedAmount, x.Recovery.ApprovedAmount,
            x.Recovery.Currency, x.Recovery.Status, x.Recovery.RaisedAt, x.Recovery.ApprovedAt, x.Recovery.ApprovedBy,
            x.Recovery.RecoveryNumber, x.CustomerClaimNumber, DocumentVersion.Encode(x.Recovery.RowVersion))).ToList();
    }

    public Task<bool> HasOpenClaimsAsync(Guid unitId, Guid entitlementId, CancellationToken cancellationToken) =>
        HasOpenClaimsAsync(unitId, [entitlementId], cancellationToken);

    public async Task<bool> HasOpenClaimsAsync(Guid unitId, IReadOnlyList<Guid> entitlementIds, CancellationToken cancellationToken)
    {
        if (entitlementIds.Count == 0) return false;
        var tenantId = tenant.TenantId;
        return await db.Claims.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Status != ClaimStatus.Closed
            && (c.UnitId == unitId || db.ClaimLines.Any(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId && l.UnitId == unitId))
            && (c.Status == ClaimStatus.Submitted || c.Status == ClaimStatus.Adjudicating
                || db.ClaimLines.Any(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId && l.UnitId == unitId
                    && l.EntitlementId != null && entitlementIds.Contains(l.EntitlementId.Value))), cancellationToken);
    }

    internal static ClaimLineDecision Decision(Data.Entities.ClaimLine line) => new(line.ClaimLineId, line.UnitId, line.CoverageFamily, line.RequestedAmount,
        line.CoveragePct, line.ApprovedAmount, line.CoverageSource, line.EntitlementId, line.DenialReason, line.ChainLength,
        line.WaitingPeriodPurchaseDate, line.FirstClaimableDate, line.WaitingPeriodNotAppliedReason);

    private sealed class ClaimLineTotals
    {
        public Guid ClaimId { get; init; }
        public decimal Requested { get; init; }
        public decimal Approved { get; init; }
        public int Lines { get; init; }
    }
}
