using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// The coverage projection carried by every entitlement ledger fact: the customer's cover only. Inbound (supplier)
/// entitlements (P2-T16) are what a vendor owes us; they never count towards the unit's warranty end or the live ids
/// the warranty check reads.
/// </summary>
public sealed record EntitlementCoverage(IReadOnlyList<Guid> LiveEntitlementIds, DateOnly? WarrantyEndMax)
{
    public static EntitlementCoverage From(IEnumerable<EntitlementCoverageEntry> entitlements)
    {
        var live = entitlements.Where(e => e.Status != EntitlementStatus.Closed && e.Direction != EntitlementDirection.Inbound).ToList();
        return new(live.Select(e => e.EntitlementId).Order().ToList(), live.Select(e => e.EndDate).Max());
    }
}

public sealed record EntitlementCoverageEntry(Guid EntitlementId, string Status, DateOnly? EndDate, string Direction = EntitlementDirection.Outbound);
