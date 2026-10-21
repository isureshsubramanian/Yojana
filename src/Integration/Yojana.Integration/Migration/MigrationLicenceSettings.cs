using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration;

/// <summary>OD-20 remains tenant configuration; the shipped default does not assume extraction rights.</summary>
public sealed class MigrationLicenceSettings(ITenantSettings settings)
{
    public const string SettingKey = "migration.extraction.default_licence_position";

    public async Task<string> DefaultPositionAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(SettingKey, cancellationToken)
            ?? MigrationLicencePosition.Unconfirmed;
        if (value is not (MigrationLicencePosition.Unconfirmed or MigrationLicencePosition.Confirmed))
            throw new InvalidOperationException($"Tenant setting '{SettingKey}' must be unconfirmed or confirmed.");
        return value;
    }
}
