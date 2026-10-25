namespace Yojana.Integration.Contracts;

/// <summary>
/// Narrow tenant-scoped capability answer used by modules that offer ERP BOM-backed settings. Implementations own
/// connection persistence; callers learn only whether at least one active connection can supply the canonical fact.
/// </summary>
public interface IErpBomCapabilityPort
{
    Task<bool> HasActiveBomSourceAsync(CancellationToken cancellationToken);
}
