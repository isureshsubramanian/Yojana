using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// Persisted claims are authoritative. Development and tests add their facts in the development-only assembly.
/// </summary>
public sealed class WarrantyClaimsPort(ClaimQueries persisted) : IWarrantyClaimsPort
{
    public async Task<bool> HasOpenClaimsAsync(Guid unitId, Guid entitlementId, CancellationToken cancellationToken) =>
        await persisted.HasOpenClaimsAsync(unitId, entitlementId, cancellationToken);

    public async Task<bool> HasOpenClaimsAsync(Guid unitId, IReadOnlyList<Guid> entitlementIds, CancellationToken cancellationToken) =>
        await persisted.HasOpenClaimsAsync(unitId, entitlementIds, cancellationToken);
}
