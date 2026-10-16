using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T05: the ADR-0001 entitlement state machine and lazy expiry.</summary>
public sealed class EntitlementStateMachineTests
{
    private static readonly string[] Events = [EntitlementEventType.EvidenceAttached, EntitlementEventType.Rebased, EntitlementEventType.Suspended, EntitlementEventType.Resumed, EntitlementEventType.Voided, EntitlementEventType.Closed];

    [Theory]
    [InlineData(EntitlementStatus.PendingEvidence, EntitlementEventType.EvidenceAttached, EntitlementStatus.Active)]
    [InlineData(EntitlementStatus.Active, EntitlementEventType.Rebased, EntitlementStatus.Active)]
    [InlineData(EntitlementStatus.Active, EntitlementEventType.Suspended, EntitlementStatus.Suspended)]
    [InlineData(EntitlementStatus.Suspended, EntitlementEventType.Resumed, EntitlementStatus.Active)]
    [InlineData(EntitlementStatus.PendingEvidence, EntitlementEventType.Voided, EntitlementStatus.Closed)]
    [InlineData(EntitlementStatus.Suspended, EntitlementEventType.Closed, EntitlementStatus.Closed)]
    public void Allowed_transitions(string from, string eventType, string to) => Assert.Equal(to, EntitlementStateMachine.Next(from, eventType));

    [Theory]
    [InlineData(EntitlementStatus.PendingEvidence, EntitlementEventType.Rebased)]
    [InlineData(EntitlementStatus.PendingEvidence, EntitlementEventType.Suspended)]
    [InlineData(EntitlementStatus.Active, EntitlementEventType.EvidenceAttached)]
    [InlineData(EntitlementStatus.Active, EntitlementEventType.Resumed)]
    [InlineData(EntitlementStatus.Suspended, EntitlementEventType.Suspended)]
    [InlineData(EntitlementStatus.Suspended, EntitlementEventType.Rebased)]
    public void Refused_transitions(string from, string eventType) => Assert.Null(EntitlementStateMachine.Next(from, eventType));

    [Fact]
    public void A_closed_entitlement_is_never_reopened() => Assert.All(Events, e => Assert.Null(EntitlementStateMachine.Next(EntitlementStatus.Closed, e)));

    [Theory]
    [InlineData(DurationBasis.Time, "2028-10-01", false, "2028-10-01", EntitlementStatus.Active)]
    [InlineData(DurationBasis.Time, "2028-10-01", false, "2028-10-02", "expired")]
    [InlineData(DurationBasis.Time, "2028-10-01", true, "2027-01-01", EntitlementStatus.Active)]
    [InlineData(DurationBasis.Usage, "2028-10-01", false, "2030-01-01", EntitlementStatus.Active)]
    [InlineData(DurationBasis.Usage, "2028-10-01", true, "2027-01-01", "expired")]
    [InlineData(DurationBasis.Both, "2028-10-01", true, "2027-01-01", "expired")]
    [InlineData(DurationBasis.Both, "2028-10-01", false, "2028-10-02", "expired")]
    [InlineData(DurationBasis.Both, "2028-10-01", false, "2027-01-01", EntitlementStatus.Active)]
    public void Expiry_is_lazy_and_follows_the_duration_basis(string basis, string end, bool usageExhausted, string today, string expected) =>
        Assert.Equal(expected, EntitlementStateMachine.Effective(EntitlementStatus.Active, DateOnly.Parse(end), basis, usageExhausted, DateOnly.Parse(today)));

    [Fact]
    public void A_suspended_entitlement_does_not_expire_while_its_clock_may_be_paused() =>
        Assert.Equal(EntitlementStatus.Suspended, EntitlementStateMachine.Effective(EntitlementStatus.Suspended, new DateOnly(2026, 1, 1), DurationBasis.Time, false, new DateOnly(2027, 1, 1)));

    [Fact]
    public void Transfer_and_supersession_reasons_are_not_available_to_close_or_void()
    {
        foreach (var reason in new[] { EntitlementClosedReason.Replaced, EntitlementClosedReason.TransferredOut, EntitlementClosedReason.ReplacedByChange, EntitlementClosedReason.Superseded })
        {
            Assert.DoesNotContain(reason, EntitlementStateMachine.CloseReasons);
            Assert.DoesNotContain(reason, EntitlementStateMachine.VoidReasons);
        }
    }
}
