namespace Yojana.Serialisation.Contracts;

public sealed record LegalEntityTimeZoneOverride(Guid LegalEntityId, string TimeZone);

public sealed record TenantTimeZonePolicy(
    string? TenantTimeZone,
    IReadOnlyList<LegalEntityTimeZoneOverride> LegalEntityOverrides,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record TimeZoneOption(string Id, string Offset, string Label);

public sealed record LegalEntityTimeZoneOption(Guid LegalEntityId, string Name, string Code);

public sealed record TenantTimeZoneHistoryRow(
    string? TenantTimeZone,
    IReadOnlyList<LegalEntityTimeZoneOverride> LegalEntityOverrides,
    string UpdatedBy,
    DateTime UpdatedAt,
    DateTime ValidFrom,
    DateTime ValidTo);

public sealed record TenantTimeZoneSettingsView(
    string? TenantTimeZone,
    IReadOnlyList<LegalEntityTimeZoneOverride> LegalEntityOverrides,
    IReadOnlyList<LegalEntityTimeZoneOption> LegalEntities,
    IReadOnlyList<TimeZoneOption> TimeZones,
    string Version,
    string? UpdatedBy,
    DateTime? UpdatedAt,
    IReadOnlyList<TenantTimeZoneHistoryRow> History);

public sealed record UpdateTenantTimeZoneSettings(
    string? TenantTimeZone,
    IReadOnlyList<LegalEntityTimeZoneOverride> LegalEntityOverrides,
    string ExpectedVersion);

/// <summary>Narrow tenant-zone read used by tenant-local scheduled work outside Serialisation.</summary>
public interface ITenantTimeZoneReadPort
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}
