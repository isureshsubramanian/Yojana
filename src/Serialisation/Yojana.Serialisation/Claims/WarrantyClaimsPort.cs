using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// Persisted claims are authoritative. The in-memory answer remains an additive development/test override so existing
/// entitlement tests can represent claims owned by a not-yet-connected external source without reaching into tables.
/// </summary>
public sealed class WarrantyClaimsPort(ClaimQueries persisted, InMemoryWarrantyPorts fallback) : IWarrantyClaimsPort
{
    public async Task<bool> HasOpenClaimsAsync(Guid unitId, Guid entitlementId, CancellationToken cancellationToken) =>
        await persisted.HasOpenClaimsAsync(unitId, entitlementId, cancellationToken)
        || await fallback.HasOpenClaimsAsync(unitId, entitlementId, cancellationToken);

    public async Task<bool> HasOpenClaimsAsync(Guid unitId, IReadOnlyList<Guid> entitlementIds, CancellationToken cancellationToken) =>
        await persisted.HasOpenClaimsAsync(unitId, entitlementIds, cancellationToken)
        || await fallback.HasOpenClaimsAsync(unitId, entitlementIds, cancellationToken);
}
