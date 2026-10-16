using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>The coverage projection carried by every entitlement ledger fact.</summary>
public sealed record EntitlementCoverage(IReadOnlyList<Guid> LiveEntitlementIds, DateOnly? WarrantyEndMax)
{
    public static EntitlementCoverage From(IEnumerable<EntitlementCoverageEntry> entitlements)
    {
        var live = entitlements.Where(e => e.Status != EntitlementStatus.Closed).ToList();
        return new(live.Select(e => e.EntitlementId).Order().ToList(), live.Select(e => e.EndDate).Max());
    }
}

public sealed record EntitlementCoverageEntry(Guid EntitlementId, string Status, DateOnly? EndDate);
