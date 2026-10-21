using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class CanonicalisationTests
{
    [Fact]
    public async Task Family_settings_default_to_the_profiling_stand_in_and_are_versioned()
    {
        var store = new VersionedSettings();
        var settings = new CanonicalFamilySettings(store);

        var defaults = await settings.GetAsync(default);

        var fallback = Assert.Single(defaults.Families);
        Assert.Equal("", fallback.ItemCodePrefix);
        Assert.True(fallback.StripHyphens);
        Assert.True(fallback.StripSpaces);
        Assert.True(fallback.StripLeadingZeros);
        Assert.Equal(ITenantSettings.MissingVersion, defaults.Version);

        var saved = await settings.SetAsync(new(
        [
            new(" pc-", StripHyphens: false, StripSpaces: true, StripLeadingZeros: false),
            new("", StripHyphens: true, StripSpaces: true, StripLeadingZeros: true),
        ], defaults.Version), default);
        Assert.Equal("1", saved.Version);
        Assert.Equal("PC-", saved.Families[0].ItemCodePrefix);

        var invalid = await Assert.ThrowsAsync<MigrationRuleException>(() => settings.SetAsync(new(
            [new("SERVER", true, true, true)], saved.Version), default));
        Assert.Equal("migration_canonical_families_invalid", invalid.Rule);
    }

    private sealed class VersionedSettings : ITenantSettings
    {
        private string? value;
        private int version;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(value);

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            this.value = value;
            version++;
            return Task.CompletedTask;
        }

        public Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(value is null ? null : new VersionedTenantSetting(value, version.ToString(), DateTime.UtcNow));

        public Task<VersionedTenantSetting> SetAsync(string key, string newValue, string expectedVersion,
            CancellationToken cancellationToken)
        {
            var current = value is null ? ITenantSettings.MissingVersion : version.ToString();
            if (current != expectedVersion)
                throw new TenantSettingVersionConflictException(key, expectedVersion, current);
            value = newValue;
            version++;
            return Task.FromResult(new VersionedTenantSetting(newValue, version.ToString(), DateTime.UtcNow));
        }
    }
}
