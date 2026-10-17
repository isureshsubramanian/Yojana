using System.Text.Json;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

/// <summary>What the policy stack decides for one claim line, before anything is written.</summary>
public sealed record ClaimLineOutcome(
    decimal CoveragePct,
    decimal ApprovedAmount,
    string CoverageSource,
    string? DenialReason,
    Guid? EntitlementId = null,
    Guid? PolicyVersionId = null,
    Guid? AccrualCohortId = null,
    string? SettlementFormula = null,
    int ChainLength = 0,
    Guid? ObligorEntityId = null,
    PolicyResolution? Resolution = null)
{
    public static ClaimLineOutcome Denied(string reason, PolicyResolution? resolution = null) =>
        new(0, 0, ClaimCoverageSource.Chargeable, reason, Resolution: resolution);
}

/// <summary>
/// Decides one claim line against the policy stack on the claim date. Adjudication applies the outcome to the stored
/// line; the adjudication preview shows the same outcome without writing, so the screen and the commit cannot disagree.
/// </summary>
public sealed class ClaimLineDecider(ClaimUnitFacts facts, PolicyResolver policies, ISettlementFormulaPort formulas)
{
    public async Task<ClaimLineOutcome> DecideAsync(Claim claim, ClaimUnitFact root, ItemSerialisationProfile? item, ClaimLine line,
        CampaignCover? campaignCover, CancellationToken cancellationToken)
    {
        var unit = line.UnitId == root.UnitId ? root : await facts.ReadAsync(line.UnitId, cancellationToken);
        var blocked = ClaimAdjudicationRules.BlockingFlag(root.OverlayFlags, campaignCover is not null)
            ?? ClaimAdjudicationRules.BlockingFlag(unit.OverlayFlags, campaignCover is not null);
        if (blocked is not null) return ClaimLineOutcome.Denied($"overlay_{blocked}");
        if (ClaimAdjudicationRules.ItemDenialReason(item, claim.ClaimDate, line.Kind) is { } itemDenialReason) return ClaimLineOutcome.Denied(itemDenialReason);
        if (campaignCover is not null)
        {
            var campaignPercentage = CampaignCoverage(campaignCover, line.Kind);
            var campaignMeasurements = JsonSerializer.Deserialize<Dictionary<string, decimal>>(line.Measurements, LedgerJson.Options) ?? [];
            var campaignSettlement = await formulas.CalculateAsync(new SettlementFormulaQuestion("full", line.RequestedAmount, campaignPercentage, campaignMeasurements), cancellationToken);
            var campaignApproved = Math.Min(line.RequestedAmount, campaignSettlement.ApprovedAmount);
            return new ClaimLineOutcome(campaignPercentage, campaignApproved, campaignCover.CoverageSource,
                campaignApproved == 0 ? "coverage_percentage_zero" : campaignApproved < line.RequestedAmount ? "partially_uncovered" : null,
                SettlementFormula: "full", ObligorEntityId: claim.ObligorEntityId);
        }

        var resolution = await policies.ResolveAsync(new PolicyQuestion(line.UnitId, unit.ItemId, line.CoverageFamily, claim.ClaimDate,
            claim.ClaimantPartyRef, Country: claim.ServiceCountry, CoverageDate: claim.ClaimDate, Direction: EntitlementDirection.Outbound), cancellationToken);
        if (resolution.Level == PolicyLevel.None || resolution.PolicyVersionId is null) return ClaimLineOutcome.Denied("no_applicable_coverage", resolution);
        if (resolution.OutOfRegion) return ClaimLineOutcome.Denied("service_country_out_of_region", resolution);
        if (resolution.EntitlementStatus == EntitlementStatus.Suspended) return ClaimLineOutcome.Denied("entitlement_suspended", resolution);

        var coverage = CoverageFor(resolution.Coverage, line.Kind);
        var measurements = JsonSerializer.Deserialize<Dictionary<string, decimal>>(line.Measurements, LedgerJson.Options) ?? [];
        var calculated = await formulas.CalculateAsync(new SettlementFormulaQuestion(resolution.SettlementFormula ?? "full", line.RequestedAmount, coverage, measurements), cancellationToken);
        var approved = Math.Min(line.RequestedAmount, calculated.ApprovedAmount);
        return new ClaimLineOutcome(coverage, approved, Source(resolution.EntitlementType, resolution.Level),
            approved == 0 ? "coverage_percentage_zero" : approved < line.RequestedAmount ? "partially_uncovered" : null,
            resolution.EntitlementId, resolution.PolicyVersionId, resolution.AccrualCohortId, resolution.SettlementFormula ?? "full",
            resolution.ChainLength, resolution.ObligorEntityId, resolution);
    }

    public static void Apply(ClaimLine line, ClaimLineOutcome outcome)
    {
        line.CoveragePct = outcome.CoveragePct;
        line.ApprovedAmount = outcome.ApprovedAmount;
        line.CoverageSource = outcome.CoverageSource;
        line.DenialReason = outcome.DenialReason;
        line.EntitlementId = outcome.EntitlementId;
        line.PolicyVersionId = outcome.PolicyVersionId;
        line.AccrualCohortId = outcome.AccrualCohortId;
        line.SettlementFormula = outcome.SettlementFormula ?? line.SettlementFormula;
        line.ChainLength = outcome.ChainLength;
    }

    private static decimal CoverageFor(CoverageTerms? coverage, string kind) => coverage is null ? 0 : kind switch
    {
        ClaimLineKind.Parts => coverage.PartsPct,
        ClaimLineKind.Labour => coverage.LabourPct,
        ClaimLineKind.Travel => coverage.TravelPct,
        _ => 0,
    };

    public static decimal CampaignCoverage(CampaignCover campaign, string kind) => kind switch
    {
        ClaimLineKind.Parts => campaign.PartsPct,
        ClaimLineKind.Labour => campaign.LabourPct,
        ClaimLineKind.Travel => campaign.TravelPct,
        _ => 0m,
    };

    private static string Source(string? entitlementType, string level) => entitlementType switch
    {
        EntitlementType.RecallCampaign => ClaimCoverageSource.Recall,
        EntitlementType.Goodwill => ClaimCoverageSource.Goodwill,
        EntitlementType.Contract or EntitlementType.Extended => ClaimCoverageSource.Contract,
        _ when level == PolicyLevel.CustomerContract => ClaimCoverageSource.Contract,
        _ => ClaimCoverageSource.Warranty,
    };
}
