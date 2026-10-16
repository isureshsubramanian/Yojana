using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T08: approval of AmendEntitlement by reason and days changed, the reduction rule, and the policy's own validation.</summary>
public sealed class AmendmentRulesTests
{
    private static readonly AmendmentApprovalPolicy Default = AmendmentRules.DefaultPolicy;

    [Theory]
    [InlineData(AmendmentReason.Purchase, 730, YojanaRole.ServiceDesk, true, false)]
    [InlineData(AmendmentReason.Correction, 30, YojanaRole.ServiceDesk, false, false)]
    [InlineData(AmendmentReason.Correction, 31, YojanaRole.ServiceManager, false, false)]
    [InlineData(AmendmentReason.SellerError, 12, YojanaRole.ServiceDesk, false, false)]
    [InlineData(AmendmentReason.Goodwill, 5, YojanaRole.ServiceManager, false, false)]
    [InlineData(AmendmentReason.Goodwill, 91, YojanaRole.ServiceManager, false, true)]
    [InlineData(AmendmentReason.DisputeSettlement, 365, YojanaRole.ServiceManager, false, true)]
    public void The_default_table_decides_by_reason_and_days(string reason, int days, string role, bool sourceDocument, bool financeReview)
    {
        var required = AmendmentRules.Required(Default, reason, days, reduction: false, certificateIssued: true);
        Assert.Equal((role, sourceDocument, financeReview, false, false), (required.Role, required.SourceDocument, required.FinanceReview, required.CustomerNotification, required.SystemOnly));
    }

    [Fact]
    public void Days_count_either_way_and_a_reduction_needs_the_manager_and_a_notification_once_a_certificate_exists()
    {
        Assert.Equal(YojanaRole.ServiceManager, AmendmentRules.Required(Default, AmendmentReason.Correction, -31, false, false).Role);
        var issued = AmendmentRules.Required(Default, AmendmentReason.Correction, -10, reduction: true, certificateIssued: true);
        Assert.Equal((YojanaRole.ServiceManager, true), (issued.Role, issued.CustomerNotification));
        var notIssued = AmendmentRules.Required(Default, AmendmentReason.Correction, -10, reduction: true, certificateIssued: false);
        Assert.Equal((YojanaRole.ServiceManager, false), (notIssued.Role, notIssued.CustomerNotification));
    }

    [Fact]
    public void Statutory_extension_is_system_only_and_an_unknown_reason_is_refused()
    {
        Assert.True(AmendmentRules.Required(Default, AmendmentReason.StatutoryExtension, 365, false, false).SystemOnly);
        Assert.Equal("reason_unknown", Assert.Throws<CommandRefusedException>(() => AmendmentRules.Required(Default, "typo", 1, false, false)).Rule);
    }

    [Fact]
    public void A_manager_satisfies_the_desk_but_not_the_other_way_round_and_roles_off_the_ladder_need_themselves()
    {
        Assert.True(AmendmentRules.Satisfies([YojanaRole.ServiceManager], YojanaRole.ServiceDesk));
        Assert.False(AmendmentRules.Satisfies([YojanaRole.ServiceDesk], YojanaRole.ServiceManager));
        Assert.False(AmendmentRules.Satisfies([YojanaRole.Technician], YojanaRole.ServiceDesk));
        Assert.False(AmendmentRules.Satisfies([YojanaRole.ServiceManager], YojanaRole.FinanceReview));
        Assert.True(AmendmentRules.Satisfies([YojanaRole.FinanceReview], YojanaRole.FinanceReview));
    }

    [Fact]
    public void Coverage_is_reduced_by_a_lower_share_a_longer_or_dropped_response_or_changed_hours()
    {
        var onSite = new CoverageTerms(100, 100, 100, "on_site", 24, "business_hours");
        Assert.False(AmendmentRules.CoverageReduced(onSite, onSite with { TravelPct = 100, ResponseHours = 8 }));
        Assert.True(AmendmentRules.CoverageReduced(onSite, onSite with { LabourPct = 50 }));
        Assert.True(AmendmentRules.CoverageReduced(onSite, onSite with { ResponseHours = 48 }));
        Assert.True(AmendmentRules.CoverageReduced(onSite, onSite with { ResponseHours = null }));
        Assert.True(AmendmentRules.CoverageReduced(onSite, onSite with { CoverageHours = "24x7" }));
    }

    [Fact]
    public void The_default_policy_is_valid_and_a_broken_one_lists_every_problem()
    {
        Assert.Empty(AmendmentRules.Validate(Default));
        var broken = Default with
        {
            ReductionRole = "boss",
            Reasons = Default.Reasons.Where(r => r.ReasonCode != AmendmentReason.Goodwill)
                .Select(r => r.ReasonCode == AmendmentReason.Correction ? r with { Tiers = [new(30, YojanaRole.ServiceDesk), new(10, YojanaRole.ServiceManager), new(60, "nobody")] } : r).ToList(),
        };
        var problems = AmendmentRules.Validate(broken);
        Assert.Contains(problems, p => p.Contains("reduction role 'boss'"));
        Assert.Contains(problems, p => p.Contains("'goodwill' has no rule"));
        Assert.Contains(problems, p => p.Contains("'correction': the last tier must have no day bound"));
        Assert.Contains(problems, p => p.Contains("'correction': day bounds must be ascending"));
        Assert.Contains(problems, p => p.Contains("'nobody' is not a role"));
    }

    [Fact]
    public void The_state_machine_amends_only_an_active_entitlement()
    {
        Assert.Equal(EntitlementStatus.Active, EntitlementStateMachine.Next(EntitlementStatus.Active, EntitlementEventType.Amended));
        Assert.Null(EntitlementStateMachine.Next(EntitlementStatus.Suspended, EntitlementEventType.Amended));
        Assert.Null(EntitlementStateMachine.Next(EntitlementStatus.PendingEvidence, EntitlementEventType.Amended));
        Assert.Null(EntitlementStateMachine.Next(EntitlementStatus.Closed, EntitlementEventType.Amended));
    }
}
