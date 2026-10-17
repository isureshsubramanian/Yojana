using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// Shows what adjudication would decide for a claim now, through the same <see cref="ClaimLineDecider"/> the handler
/// uses, on untracked copies so nothing is written. The refusals mirror the handler's; if they drift, the screen would
/// offer an approve that the command then refuses, so change both together.
/// </summary>
public sealed partial class ClaimAdjudicationPreviewQuery(SerialisationDbContext db, ClaimLineDecider decider, ClaimUnitFacts facts, IItemQueryPort items,
    IEntitlementRecoveryQuery recoveries, RecoverySettings recoverySettings, ICampaignCoverPort campaigns, ITenantContext tenant)
{
    public async Task<ClaimAdjudicationPreview?> PreviewAsync(Guid claimId, string lowConfidenceReviewerRole, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClaimId == claimId, cancellationToken);
        if (claim is null) return null;
        var lines = await db.ClaimLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.ClaimId == claimId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var evidence = await db.ClaimEvidence.AsNoTracking().Where(e => e.TenantId == tenantId && e.ClaimId == claimId).Select(e => e.Kind).ToListAsync(cancellationToken);
        var root = await facts.ReadAsync(claim.UnitId, cancellationToken);
        var namedCampaign = claim.CampaignId is { } campaignId
            ? await campaigns.FindAsync(campaignId, claim.UnitId, claim.ClaimDate, cancellationToken)
            : null;
        var liveCampaign = namedCampaign is { Selected: true } && namedCampaign.CoversOn(claim.ClaimDate) ? namedCampaign : null;
        var item = await items.GetAsync(root.ItemId, cancellationToken);
        var unitIds = lines.Select(l => l.UnitId).Append(claim.UnitId).Distinct().ToList();
        var serials = new Dictionary<Guid, string>(await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken));
        var itemIds = await db.Current.AsNoTracking().Where(u => u.TenantId == tenantId && unitIds.Contains(u.UnitId)).ToDictionaryAsync(u => u.UnitId, u => u.ItemId, cancellationToken);
        var decided = claim.Status is not (ClaimStatus.Submitted or ClaimStatus.Adjudicating);
        // Lines decided (or about to be decided) under the campaign show the campaign where a policy would otherwise be named.
        var campaignLines = decided ? claim.CampaignId is not null && claim.CampaignCoverReason is null && namedCampaign is not null : liveCampaign is not null;

        // A recorded line keeps only its policy version id; name it so the screen shows which policy covered it.
        var recordedVersionIds = decided ? lines.Select(l => l.PolicyVersionId).OfType<Guid>().Distinct().ToList() : [];
        var recordedPolicies = await db.PolicyVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && recordedVersionIds.Contains(v.PolicyVersionId))
            .ToDictionaryAsync(v => v.PolicyVersionId, v => (v.PolicyCode, v.Version), cancellationToken);
        var previews = new List<ClaimLinePreview>();
        var outcomes = new List<(ClaimLine Line, ClaimLineOutcome Outcome)>();
        foreach (var line in lines)
        {
            // A decided claim shows what was recorded; an open one shows what approve would record now.
            var outcome = decided
                ? new ClaimLineOutcome(line.CoveragePct, line.ApprovedAmount, line.CoverageSource, line.DenialReason, line.EntitlementId, line.PolicyVersionId, line.AccrualCohortId, line.SettlementFormula, line.ChainLength)
                : await decider.DecideAsync(claim, root, item, line, liveCampaign, cancellationToken);
            outcomes.Add((line, outcome));
            var resolution = outcome.Resolution;
            var (start, end) = await TermAsync(outcome.EntitlementId, cancellationToken);
            previews.Add(new ClaimLinePreview(line.ClaimLineId, line.Sequence, line.UnitId, serials.GetValueOrDefault(line.UnitId),
                itemIds.TryGetValue(line.UnitId, out var itemId) ? itemId : null, line.UnitId != claim.UnitId, line.Kind, line.CoverageFamily, line.Description,
                line.RequestedAmount, outcome.CoveragePct, outcome.ApprovedAmount, outcome.CoverageSource,
                campaignLines ? "campaign" : resolution?.Level ?? (outcome.EntitlementId is null ? PolicyLevel.None : PolicyLevel.UnitEntitlement),
                campaignLines ? namedCampaign!.Code : resolution?.PolicyCode ?? Recorded(outcome.PolicyVersionId)?.PolicyCode,
                campaignLines ? null : resolution?.Version ?? Recorded(outcome.PolicyVersionId)?.Version, outcome.EntitlementId, start ?? resolution?.StartDate, end ?? resolution?.EndDate, outcome.DenialReason,
                campaignLines ? [new PolicyStackStep("campaign", "matched", $"{namedCampaign!.Code} · {namedCampaign.Name} · parts {namedCampaign.PartsPct:0.##}% · labour {namedCampaign.LabourPct:0.##}% · travel {namedCampaign.TravelPct:0.##}%")]
                    : resolution is null ? [] : Stack(resolution, serials)));
        }

        (string PolicyCode, int Version)? Recorded(Guid? versionId) =>
            versionId is { } id && recordedPolicies.TryGetValue(id, out var policy) ? policy : null;

        var covered = outcomes.Where(o => o.Outcome.ApprovedAmount > 0).ToList();
        var sources = covered.Select(o => o.Outcome.CoverageSource).Distinct().ToList();
        var cohorts = covered.Select(o => o.Outcome.AccrualCohortId).OfType<Guid>().Distinct().ToList();
        var obligors = covered.Select(o => o.Outcome.ObligorEntityId).OfType<Guid>().Distinct().ToList();
        string? refusal = null, step = null;
        if (sources.Count > 1) (refusal, step) = ("mixed_coverage_sources", "split the lines into one claim per coverage source");
        else if (cohorts.Count > 1) (refusal, step) = ("multiple_accrual_cohorts", "split the lines by accrual cohort");
        else if (obligors.Count > 1) (refusal, step) = ("multiple_obligors", "split the lines by obligor");
        else if (!decided && claim.Remedy == ClaimRemedy.Replacement && ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate) && item?.SuccessorItemId is null)
        {
            (refusal, step) = ("replacement_successor_required", "choose repair or goodwill");
        }

        var requested = lines.Sum(l => l.RequestedAmount);
        var approved = outcomes.Sum(o => o.Outcome.ApprovedAmount);
        var outcomeStatus = decided ? claim.Status
            : outcomes.All(o => o.Outcome.DenialReason is null && o.Outcome.ApprovedAmount == o.Line.RequestedAmount) ? ClaimStatus.Approved
            : approved > 0 ? ClaimStatus.PartiallyApproved : ClaimStatus.Denied;
        var source = sources.Count == 1 ? sources[0] : decided ? claim.CoverageSource : ClaimCoverageSource.Chargeable;
        var campaignCoverReason = decided ? claim.CampaignCoverReason : claim.CampaignId is null || liveCampaign is not null
            ? null : namedCampaign?.NotCoveredReason(claim.ClaimDate) ?? ClaimCampaignCoverReason.Closed;
        var campaignCovered = claim.CampaignId is not null && campaignCoverReason is null
            && source is ClaimCoverageSource.Recall or ClaimCoverageSource.Goodwill;

        var entitlementIds = outcomes.Select(o => o.Outcome.EntitlementId).OfType<Guid>().Distinct().ToList();
        var lowConfidence = entitlementIds.Count > 0 && await db.Entitlements.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && entitlementIds.Contains(e.EntitlementId) && e.Confidence == Confidence.Low, cancellationToken);

        var recoveryPreviews = new List<RecoveryPreview>();
        var recoveryPolicy = !decided && outcomeStatus is ClaimStatus.Approved or ClaimStatus.PartiallyApproved
            ? await recoverySettings.GetAsync(cancellationToken)
            : null;
        string? notRaisedReason = decided ? claim.RecoveryNotRaisedReason
            : recoveryPolicy is null ? null
            : liveCampaign is not null
                ? recoveryPolicy.CampaignClaims == RecoveryCampaignClaims.NoRecovery ? RecoveryNotRaisedReason.CampaignClaim : null
                : source == ClaimCoverageSource.Recall ? RecoveryNotRaisedReason.RecallClaim : null;
        if (notRaisedReason is null && !decided && outcomeStatus is ClaimStatus.Approved or ClaimStatus.PartiallyApproved)
        {
            foreach (var (line, outcome) in covered)
            {
                var decision = RecoverySelection.Select(recoveryPolicy!, claim.UnitId, line, await recoveries.FindAsync(claim.UnitId, line.UnitId, claim.ClaimDate, cancellationToken));
                notRaisedReason ??= decision.NotRaisedReason;
                foreach (var cover in decision.Covers)
                {
                    var (termStart, termEnd) = await TermAsync(cover.EntitlementId, cancellationToken);
                    if (!serials.ContainsKey(cover.UnitId))
                    {
                        foreach (var pair in await UnitSerials.DisplayAsync(db, tenantId, [cover.UnitId], cancellationToken)) serials[pair.Key] = pair.Value;
                    }

                    recoveryPreviews.Add(new RecoveryPreview(cover.UnitId, serials.GetValueOrDefault(cover.UnitId), cover.VendorPartyRef, outcome.ApprovedAmount, termStart, termEnd,
                        termStart is { } s && termEnd is { } e ? Months(s, e.AddDays(1)) : null, termStart is { } from ? Months(from, claim.ClaimDate) + 1 : null));
                }
            }
        }

        var checks = Checks(claim, root, item, outcomes, evidence, campaignCovered, namedCampaign, campaignCoverReason);
        return new ClaimAdjudicationPreview(claim.ClaimId, claim.ClaimNumber, outcomeStatus, source, claim.Currency, requested, approved, previews, checks,
            campaignCovered || source == ClaimCoverageSource.Recall || cohorts.Count != 1 ? null : cohorts[0], obligors.Count == 1 ? obligors[0] : claim.ObligorEntityId,
            recoveryPreviews, requested - approved, refusal, step, lowConfidence ? lowConfidenceReviewerRole : null,
            ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate) ? item?.SuccessorItemId : null, ClaimManualDenialReason.All.Order(StringComparer.Ordinal).ToList(),
            recoveryPreviews.Count == 0 ? notRaisedReason : null, claim.CampaignId, namedCampaign?.Code, namedCampaign?.Name,
            decided ? claim.ReserveTag : liveCampaign?.ReserveTag, campaignCoverReason);
    }

    private static List<ClaimCheck> Checks(Claim claim, ClaimUnitFact root, ItemSerialisationProfile? item,
        List<(ClaimLine Line, ClaimLineOutcome Outcome)> outcomes, List<string> evidence, bool campaignCovered,
        CampaignCover? campaign, string? campaignCoverReason)
    {
        var checks = new List<ClaimCheck>();
        var flag = ClaimAdjudicationRules.BlockingFlag(root.OverlayFlags, campaignCovered);
        checks.Add(new("overlay_flags", flag ?? (root.OverlayFlags.Count == 0 ? "clear" : string.Join(", ", root.OverlayFlags)), flag is null ? "ok" : "fail"));
        if (campaign is not null)
            checks.Add(new("campaign_cover", campaignCoverReason ?? campaign.Code, campaignCoverReason is null ? "ok" : "warn"));
        var outOfRegion = outcomes.Any(o => o.Outcome.Resolution?.OutOfRegion == true);
        checks.Add(new("service_country", claim.ServiceCountry, outOfRegion ? "fail" : "ok"));
        var term = outcomes.Select(o => o.Outcome.Resolution).FirstOrDefault(r => r?.StartDate is not null && r.EndDate is not null);
        if (term is { StartDate: { } start, EndDate: { } end })
        {
            checks.Add(new("claim_date_in_term", $"{Months(start, claim.ClaimDate) + 1}/{Months(start, end.AddDays(1))}", claim.ClaimDate >= start && claim.ClaimDate <= end ? "ok" : "fail"));
        }

        checks.Add(new("chain_length", outcomes.Select(o => o.Outcome.ChainLength).DefaultIfEmpty(0).Max().ToString(System.Globalization.CultureInfo.InvariantCulture), "ok"));
        var itemReason = ClaimAdjudicationRules.ItemDenialReason(item, claim.ClaimDate, ClaimLineKind.Parts);
        checks.Add(new("item_service_status", itemReason ?? (ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate) ? "discontinued" : "serviceable"), itemReason is null ? "ok" : "fail"));
        checks.Add(new("remedy", claim.Remedy, "ok"));
        checks.Add(new("consumer_choice", evidence.Contains(ClaimEvidenceKind.ConsumerRemedyChoice) ? "recorded" : "missing", evidence.Contains(ClaimEvidenceKind.ConsumerRemedyChoice) ? "ok" : "fail"));
        return checks;
    }

    private async Task<(DateOnly? Start, DateOnly? End)> TermAsync(Guid? entitlementId, CancellationToken cancellationToken)
    {
        if (entitlementId is not { } id) return (null, null);
        var tenantId = tenant.TenantId;
        var term = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.EntitlementId == id)
            .Select(e => new { e.StartDate, e.EndDate }).FirstOrDefaultAsync(cancellationToken);
        return (term?.StartDate, term?.EndDate);
    }

    private static int Months(DateOnly from, DateOnly to) => Math.Max(0, (to.Year - from.Year) * 12 + to.Month - from.Month - (to.Day < from.Day ? 1 : 0));

    /// <summary>The resolver's trail as one step per stack level, ids replaced by serials a person can read.</summary>
    private static List<PolicyStackStep> Stack(PolicyResolution resolution, IReadOnlyDictionary<Guid, string> serials)
    {
        var matchedIndex = PolicyLevel.ResolutionOrder.ToList().IndexOf(resolution.Level);
        var steps = new List<PolicyStackStep>();
        for (var i = 0; i < PolicyLevel.ResolutionOrder.Count; i++)
        {
            var level = PolicyLevel.ResolutionOrder[i];
            var prefix = level switch
            {
                PolicyLevel.UnitEntitlement => "unit",
                PolicyLevel.ParentEntitlement => "parent",
                PolicyLevel.CustomerContract => "customer contract",
                _ => "product policy",
            };
            var entries = resolution.Trail.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => Readable(t, serials)).ToList();
            var outcome = matchedIndex >= 0 && i == matchedIndex ? "matched" : matchedIndex >= 0 && i > matchedIndex ? "not_reached" : "no_match";
            steps.Add(new PolicyStackStep(level, outcome, outcome == "not_reached" ? "" : string.Join("; ", entries)));
        }

        var region = resolution.Trail.FirstOrDefault(t => t.StartsWith("region", StringComparison.Ordinal));
        if (region is not null) steps.Add(new PolicyStackStep("region", "out_of_region", region));
        return steps;
    }

    private static string Readable(string entry, IReadOnlyDictionary<Guid, string> serials) =>
        MultipleSpaces().Replace(GuidPattern().Replace(entry, m => Guid.TryParse(m.Value, out var id) && serials.TryGetValue(id, out var serial) ? serial : ""), " ").Trim();

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
