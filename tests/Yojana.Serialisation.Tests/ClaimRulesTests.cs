using System.Text.Json;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Tests;

public sealed class ClaimRulesTests
{
    [Theory]
    [InlineData(ClaimStatus.Submitted, ClaimEventType.Adjudicating, ClaimStatus.Adjudicating)]
    [InlineData(ClaimStatus.Adjudicating, ClaimStatus.Approved, ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Adjudicating, ClaimStatus.PartiallyApproved, ClaimStatus.PartiallyApproved)]
    [InlineData(ClaimStatus.Adjudicating, ClaimStatus.Denied, ClaimStatus.Denied)]
    [InlineData(ClaimStatus.Approved, ClaimEventType.Settled, ClaimStatus.Settled)]
    [InlineData(ClaimStatus.Denied, ClaimEventType.Settled, ClaimStatus.Settled)]
    [InlineData(ClaimStatus.Settled, ClaimEventType.Closed, ClaimStatus.Closed)]
    public void Allowed_claim_transitions(string from, string eventType, string to) => Assert.Equal(to, ClaimStateMachine.Next(from, eventType));

    [Theory]
    [InlineData(ClaimStatus.Submitted, ClaimEventType.Settled)]
    [InlineData(ClaimStatus.Approved, ClaimEventType.Adjudicating)]
    [InlineData(ClaimStatus.Closed, ClaimEventType.Adjudicating)]
    [InlineData(ClaimStatus.Closed, ClaimEventType.Settled)]
    public void Illegal_claim_transitions_are_refused(string from, string eventType) => Assert.Null(ClaimStateMachine.Next(from, eventType));

    [Fact]
    public void Aggregate_refusal_names_the_transition_rule()
    {
        var claim = new Claim
        {
            TenantId = new TenantId(Guid.NewGuid()), ClaimId = Guid.NewGuid(), ClaimNumber = "CLM-2026-00001", CommandId = Guid.NewGuid(), UnitId = Guid.NewGuid(),
            ServiceCountry = "DE", Currency = "EUR", CoverageSource = ClaimCoverageSource.Warranty, Remedy = ClaimRemedy.Repair,
            Status = ClaimStatus.Submitted, SubmittedBy = "test",
        };
        var refusal = Assert.Throws<CommandRefusedException>(() => ClaimLedger.Transition(claim, ClaimEventType.Closed));
        Assert.Equal("claim_transition_not_allowed", refusal.Rule);
    }

    [Fact]
    public void Policy_resolution_order_is_unit_parent_contract_product() => Assert.Equal(
        [PolicyLevel.UnitEntitlement, PolicyLevel.ParentEntitlement, PolicyLevel.CustomerContract, PolicyLevel.ProductPolicy],
        PolicyLevel.ResolutionOrder);

    [Theory]
    [InlineData(OverlayFlag.Lost)]
    [InlineData(OverlayFlag.Stolen)]
    [InlineData(OverlayFlag.WrittenOff)]
    [InlineData(OverlayFlag.CloneSeen)]
    [InlineData(OverlayFlag.Recalled)]
    [InlineData(OverlayFlag.AuthenticityFailed)]
    public void Every_claim_overlay_blocks_with_its_name(string flag) => Assert.Equal(flag, ClaimAdjudicationRules.BlockingFlag([flag]));

    [Fact]
    public void Campaign_cover_ignores_recalled_but_not_other_blocking_flags()
    {
        Assert.Null(ClaimAdjudicationRules.BlockingFlag([OverlayFlag.Recalled], recalledIsCovered: true));
        Assert.Equal(OverlayFlag.Lost, ClaimAdjudicationRules.BlockingFlag([OverlayFlag.Recalled, OverlayFlag.Lost], recalledIsCovered: true));
    }

    [Theory]
    [InlineData(ClaimLineKind.Parts, 100)]
    [InlineData(ClaimLineKind.Labour, 50)]
    [InlineData(ClaimLineKind.Travel, 0)]
    [InlineData("other", 0)]
    public void Campaign_cover_uses_the_campaign_percentage_for_supported_line_kinds(string kind, decimal expected)
    {
        var campaign = new CampaignCover(Guid.NewGuid(), "RC-1", "Recall", ClaimCoverageSource.Recall, "reserve", CampaignStatus.Active,
            CampaignReason.Safety, null, true, 100m, 50m, 0m);

        Assert.Equal(expected, ClaimLineDecider.CampaignCoverage(campaign, kind));
    }

    [Fact]
    public void Campaign_cover_includes_its_last_day_and_names_ended_or_closed_fallbacks()
    {
        var lastDay = new DateOnly(2026, 9, 1);
        var active = new CampaignCover(Guid.NewGuid(), "RC-1", "Recall", ClaimCoverageSource.Recall, "reserve", CampaignStatus.Active,
            CampaignReason.Safety, lastDay, true, 100m, 100m, 100m);
        var closed = active with { Status = CampaignStatus.Closed };

        Assert.True(active.CoversOn(lastDay));
        Assert.False(active.CoversOn(lastDay.AddDays(1)));
        Assert.Equal(ClaimCampaignCoverReason.Ended, active.NotCoveredReason(lastDay.AddDays(1)));
        Assert.Equal(ClaimCampaignCoverReason.Closed, closed.NotCoveredReason(lastDay));
    }

    [Fact]
    public void Recovery_policy_from_before_campaign_recovery_defaults_to_no_recovery()
    {
        var policy = JsonSerializer.Deserialize<RecoveryPolicy>(
            """{"wholeUnitLines":"single_covered_part","lineKinds":["parts"],"vendorAnswerDueDays":7}""", LedgerJson.Options);

        Assert.Equal(RecoveryCampaignClaims.NoRecovery, policy!.CampaignClaims);
    }

    [Fact]
    public void Low_confidence_cover_names_the_required_reviewer_rule()
    {
        var refusal = Assert.Throws<CommandRefusedException>(() =>
            ClaimAdjudicationRules.RequireLowConfidenceReviewer(null, "reviewer-1", YojanaRole.ServiceManager));

        Assert.Equal("low_confidence_reviewer_required", refusal.Rule);
        Assert.Contains(YojanaRole.ServiceManager, refusal.Message);
    }

    [Fact]
    public void Low_confidence_cover_accepts_the_named_reviewer_role()
    {
        ClaimAdjudicationRules.RequireLowConfidenceReviewer(
            new ClaimReviewer("reviewer-1", [YojanaRole.ServiceManager]), "reviewer-1", YojanaRole.ServiceManager);
    }

    [Fact]
    public void Sales_block_alone_does_not_deny_service_lines()
    {
        var item = Item() with { SalesBlocked = true };

        Assert.Null(ClaimAdjudicationRules.ItemDenialReason(item, new DateOnly(2026, 9, 1), ClaimLineKind.Parts));
    }

    [Theory]
    [InlineData(ClaimLineKind.Parts)]
    [InlineData(ClaimLineKind.Labour)]
    public void Service_block_denies_parts_and_labour_with_a_named_reason(string lineKind)
    {
        var item = Item() with { ServiceBlocked = true };

        Assert.Equal(ClaimDenialReason.ItemServiceBlocked,
            ClaimAdjudicationRules.ItemDenialReason(item, new DateOnly(2026, 9, 1), lineKind));
    }

    [Fact]
    public void Claim_after_last_service_date_is_denied_but_travel_is_not()
    {
        var item = Item() with { LastServiceDate = new DateOnly(2026, 8, 31) };

        Assert.Equal(ClaimDenialReason.AfterLastServiceDate,
            ClaimAdjudicationRules.ItemDenialReason(item, new DateOnly(2026, 9, 1), ClaimLineKind.Parts));
        Assert.Null(ClaimAdjudicationRules.ItemDenialReason(item, new DateOnly(2026, 9, 1), ClaimLineKind.Travel));
    }

    [Fact]
    public void Last_service_date_itself_remains_serviceable()
    {
        var item = Item() with { LastServiceDate = new DateOnly(2026, 9, 1) };

        Assert.Null(ClaimAdjudicationRules.ItemDenialReason(item, new DateOnly(2026, 9, 1), ClaimLineKind.Labour));
    }

    [Fact]
    public async Task Full_formula_applies_the_line_coverage_percentage()
    {
        var result = await new FullSettlementFormula().CalculateAsync(new SettlementFormulaQuestion("full", 120m, 75m, new Dictionary<string, decimal>()), CancellationToken.None);
        Assert.Equal(90m, result.ApprovedAmount);
    }

    [Fact]
    public async Task Pro_rata_formula_is_explicitly_unavailable()
    {
        var ex = await Assert.ThrowsAsync<CommandRefusedException>(() => new FullSettlementFormula().CalculateAsync(
            new SettlementFormulaQuestion("pro_rata(usage)", 120m, 100m, new Dictionary<string, decimal>()), CancellationToken.None));
        Assert.Equal("settlement_formula_not_available", ex.Rule);
    }

    [Fact]
    public void Twelve_month_inbound_cover_is_live_in_month_nine_and_not_month_fifteen()
    {
        var entitlement = new Entitlement
        {
            TenantId = new TenantId(Guid.NewGuid()), EntitlementId = Guid.NewGuid(), UnitId = Guid.NewGuid(), Scope = EntitlementScope.Unit,
            Type = EntitlementType.Manufacturer, CoverageFamily = CoverageFamilyCode.Supplier, Direction = EntitlementDirection.Inbound,
            ObligorEntityId = Guid.NewGuid(), PayerPartyRef = Guid.NewGuid(), PolicyVersionId = Guid.NewGuid(), StartTrigger = StartTrigger.Receipt,
            StartDate = new DateOnly(2025, 1, 1), StartTimeZone = "UTC", EndDate = new DateOnly(2025, 12, 31), DurationValue = 12,
            DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp, DurationBasis = DurationBasis.Time,
            Transferable = Transferability.No, AssuranceOrService = AssuranceOrService.Assurance, Status = EntitlementStatus.Active,
            Confidence = Confidence.High, Provenance = EntitlementProvenance.Created,
        };

        Assert.True(RecoveryEligibility.IsLive(entitlement, new DateOnly(2025, 9, 1)));
        Assert.False(RecoveryEligibility.IsLive(entitlement, new DateOnly(2026, 3, 1)));
    }

    [Theory]
    [InlineData(DocumentNumbers.Claim, 412, "CLM-2026-00412")]
    [InlineData(DocumentNumbers.Recovery, 31, "RCV-2026-0031")]
    public void Document_numbers_carry_series_year_and_padded_value(string series, int value, string expected) =>
        Assert.Equal(expected, DocumentNumbers.Format(series, 2026, value));

    private static ItemSerialisationProfile Item() => new(Guid.NewGuid(), "ITEM-1", ItemSerialisationProfile.TrackingModes.Serial,
        SerialisationPoint.ProductionRelease, "standard_10y", ItemSerialisationProfile.ReusePolicies.Never,
        ItemSerialisationProfile.Completeness.Block, IdentifierScheme.Internal);
}
