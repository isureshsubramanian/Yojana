using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class WarrantyCheckTests
{
    private static readonly Guid Unit = Guid.NewGuid();

    [Fact]
    public void Active_rows_cover_the_requested_day_and_return_the_latest_end()
    {
        var result = WarrantyCheck.Evaluate(Unit, new DateOnly(2026, 10, 17), null,
        [
            Row("base", EntitlementStatus.Active, new(2026, 1, 1), new(2026, 12, 31)),
            Row("travel", EntitlementStatus.Active, new(2026, 6, 1), new(2027, 5, 31)),
        ]);

        Assert.True(result.Covered);
        Assert.Equal(2, result.CoveringEntitlements.Count);
        Assert.Equal(new DateOnly(2027, 5, 31), result.EndDate);
        Assert.Equal(226, result.DaysRemaining);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(EntitlementStatus.Suspended, "2026-01-01", "2026-12-31", WarrantyCheckReason.Suspended)]
    [InlineData(EntitlementStatus.PendingEvidence, null, null, WarrantyCheckReason.PendingStart)]
    [InlineData(EntitlementStatus.Active, "2027-01-01", "2027-12-31", WarrantyCheckReason.PendingStart)]
    [InlineData(EntitlementStatus.Active, "2025-01-01", "2025-12-31", WarrantyCheckReason.Expired)]
    public void Refusal_reason_explains_why_cover_is_not_current(string status, string? start, string? end, string reason)
    {
        var row = Row("base", status, start is null ? null : DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end));
        var result = WarrantyCheck.Evaluate(Unit, new DateOnly(2026, 10, 17), "base", [row]);

        Assert.False(result.Covered);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Coverage_projection_keeps_only_live_ids_and_their_max_end()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var coverage = EntitlementCoverage.From(
        [
            new(first, EntitlementStatus.Active, new DateOnly(2026, 12, 31)),
            new(Guid.NewGuid(), EntitlementStatus.Closed, new DateOnly(2030, 1, 1)),
            new(second, EntitlementStatus.PendingEvidence, null),
        ]);

        Assert.Equal(new[] { first, second }.Order(), coverage.LiveEntitlementIds);
        Assert.Equal(new DateOnly(2026, 12, 31), coverage.WarrantyEndMax);
    }

    private static WarrantyCheckRow Row(string family, string status, DateOnly? start, DateOnly? end) =>
        new(Guid.NewGuid(), family, "commercial", status, start, end, DurationBasis.Time, "{}");
}
