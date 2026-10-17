using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;

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
            TenantId = new TenantId(Guid.NewGuid()), ClaimId = Guid.NewGuid(), CommandId = Guid.NewGuid(), UnitId = Guid.NewGuid(),
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
}
