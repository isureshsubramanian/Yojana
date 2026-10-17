using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

public static class RecoveryEligibility
{
    public static bool IsLive(Entitlement entitlement, DateOnly on) =>
        entitlement.Direction == EntitlementDirection.Inbound && entitlement.PayerPartyRef is not null
        && entitlement.StartDate is { } start && start <= on && (entitlement.EndDate is null || entitlement.EndDate >= on)
        && (entitlement.Status != EntitlementStatus.Closed || entitlement.ClosedEffective is null || entitlement.ClosedEffective >= on);
}
