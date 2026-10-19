using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Imports;

public sealed class TenantImportFileSettings(ITenantSettings settings, ImportFileStorageOptions options)
{
    public const string Key = "imports.file_storage";
    public const string Database = "database";
    public const string File = "file";
    public const int DefaultArchiveAfterDays = 90;

    public async Task<ImportFileStorageSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        var policy = stored is null ? Default() : Deserialise(stored.Value);
        return new(policy.Storage, policy.ArchiveStore, policy.ArchiveAge, policy.ArchiveAgeUnit,
            options.ArchiveStores.Select(x => new ImportFileArchiveStoreOption(x.Name, x.Kind)).ToList(),
            stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ImportFileStorageSettings> SetAsync(UpdateImportFileStorageSettings update,
        CancellationToken cancellationToken)
    {
        Validate(update.Storage, update.ArchiveStore, update.ArchiveAge, update.ArchiveAgeUnit);
        var policy = new Policy(update.Storage, update.ArchiveStore, update.ArchiveAge, update.ArchiveAgeUnit);
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), update.ExpectedVersion,
            cancellationToken);
        return await GetAsync(cancellationToken);
    }

    internal async Task<Policy> PolicyAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } value ? Deserialise(value) : Default();

    private Policy Deserialise(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var storage = root.GetProperty("storage").GetString() ?? string.Empty;
        // P7-T17 stored only a day count. Reading it as the new day-based shape is the tenant-data migration;
        // the next settings save persists the expanded representation without changing its meaning.
        var age = root.TryGetProperty("archive_age", out var ageValue)
            ? ageValue.GetInt32()
            : root.TryGetProperty("archive_after_days", out var daysValue) ? daysValue.GetInt32() : DefaultArchiveAfterDays;
        var unit = root.TryGetProperty("archive_age_unit", out var unitValue)
            ? unitValue.GetString() ?? ImportFileArchiveAgeUnit.Days : ImportFileArchiveAgeUnit.Days;
        var store = root.TryGetProperty("archive_store", out var storeValue)
            ? storeValue.GetString() ?? string.Empty : options.ArchiveStores.FirstOrDefault()?.Name ?? string.Empty;
        Validate(storage, store, age, unit);
        return new(storage, store, age, unit);
    }

    private void Validate(string storage, string archiveStore, int age, string unit)
    {
        if (storage is not (Database or File))
            throw new CommandRefusedException("import_file_storage_invalid", "Import file storage must be file or database.");
        if (!options.ArchiveStores.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, archiveStore)))
            throw new CommandRefusedException("import_file_archive_store_invalid", "Choose a configured import-file archive store.");
        if (age is < 0 or > 36500)
            throw new CommandRefusedException("import_file_archive_age_invalid", "Archive age must be between 0 and 36,500.");
        if (unit is not (ImportFileArchiveAgeUnit.Days or ImportFileArchiveAgeUnit.Months or ImportFileArchiveAgeUnit.Years))
            throw new CommandRefusedException("import_file_archive_age_unit_invalid", "Archive age unit must be days, months or years.");
    }

    internal sealed record Policy(string Storage, string ArchiveStore, int ArchiveAge, string ArchiveAgeUnit)
    {
        public DateTime Cutoff(DateTime now) => ArchiveAgeUnit switch
        {
            ImportFileArchiveAgeUnit.Days => now.AddDays(-ArchiveAge),
            ImportFileArchiveAgeUnit.Months => now.AddMonths(-ArchiveAge),
            ImportFileArchiveAgeUnit.Years => now.AddYears(-ArchiveAge),
            _ => throw new InvalidOperationException("Stored import-file archive age unit is invalid."),
        };
    }

    private Policy Default() => new(Database, options.ArchiveStores.FirstOrDefault()?.Name ?? string.Empty,
        DefaultArchiveAfterDays, ImportFileArchiveAgeUnit.Days);
}
