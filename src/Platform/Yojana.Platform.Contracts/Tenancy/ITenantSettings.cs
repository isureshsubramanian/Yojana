namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// Per-tenant settings (<c>tenant_setting</c>, key → value text) for the current tenant scope. Modules keep their
/// configurable profiles here under a dotted key they own (for example <c>search.metadata.unit</c>) and never read
/// the platform's tables directly. A missing key returns null; the module applies its default.
/// </summary>
public interface ITenantSettings
{
    const string MissingVersion = "missing";

    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Reads a setting together with the token required by an edit.</summary>
    Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This tenant-settings implementation does not expose edit versions.");

    /// <summary>Writes only when <paramref name="expectedVersion"/> is still current.</summary>
    Task<VersionedTenantSetting> SetAsync(string key, string value, string expectedVersion,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This tenant-settings implementation does not support versioned edits.");

    /// <summary>All temporal versions of a setting, including the current row, newest first.</summary>
    Task<IReadOnlyList<TenantSettingVersion>> HistoryAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TenantSettingVersion>>([]);
}

public sealed record TenantSettingVersion(string Value, DateTime ValidFrom, DateTime ValidTo);

public sealed record VersionedTenantSetting(string Value, string Version, DateTime UpdatedAt);

public sealed class TenantSettingVersionConflictException(string key, string expectedVersion, string currentVersion)
    : Exception($"Tenant setting '{key}' changed after version '{expectedVersion}' was read.")
{
    public string Key { get; } = key;
    public string ExpectedVersion { get; } = expectedVersion;
    public string CurrentVersion { get; } = currentVersion;
}
