namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// Per-tenant settings (<c>tenant_setting</c>, key → value text) for the current tenant scope. Modules keep their
/// configurable profiles here under a dotted key they own (for example <c>search.metadata.unit</c>) and never read
/// the platform's tables directly. A missing key returns null; the module applies its default.
/// </summary>
public interface ITenantSettings
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}
