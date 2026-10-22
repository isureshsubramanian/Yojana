using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Validation;

public sealed class ValidationSettings(ITenantSettings settings)
{
    public const string SettingKey = "migration.validation.settings";
    private static readonly int[] DefaultBands = [1, 3, 5];

    public async Task<ValidationSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var value = stored?.Value is null ? new Stored(0.1m, 200, DefaultBands) :
            JsonSerializer.Deserialize<Stored>(stored.Value) ?? throw new InvalidOperationException(
                $"Tenant setting '{SettingKey}' is invalid.");
        Validate(value.HistoricalTolerancePercent, value.SampleSize, value.AgeBandYears);
        return new(value.HistoricalTolerancePercent, value.SampleSize, value.AgeBandYears,
            stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ValidationSettingsView> SetAsync(UpdateValidationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request.HistoricalTolerancePercent, request.SampleSize, request.AgeBandYears);
        var value = new Stored(request.HistoricalTolerancePercent, request.SampleSize,
            request.AgeBandYears.ToArray());
        var stored = await settings.SetAsync(SettingKey, JsonSerializer.Serialize(value),
            request.ExpectedVersion, cancellationToken);
        return new(value.HistoricalTolerancePercent, value.SampleSize, value.AgeBandYears, stored.Version);
    }

    private static void Validate(decimal tolerance, int sampleSize, IReadOnlyList<int> bands)
    {
        if (tolerance < 0 || tolerance > 100)
            throw new ValidationRuleException("migration_validation_tolerance_invalid",
                "Historical tolerance must be between 0 and 100 percent.");
        if (sampleSize is < 100 or > 300)
            throw new ValidationRuleException("migration_validation_sample_size_invalid",
                "Sample size must be between 100 and 300.");
        if (bands.Count is < 1 or > 10 || bands.Any(value => value < 1) ||
            bands.Zip(bands.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new ValidationRuleException("migration_validation_age_bands_invalid",
                "Age-band year boundaries must be positive and strictly increasing.");
    }

    private sealed record Stored(decimal HistoricalTolerancePercent, int SampleSize, int[] AgeBandYears);
}
