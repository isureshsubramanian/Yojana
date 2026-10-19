using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Imports;

public sealed class TenantImportFileSettings(ITenantSettings settings)
{
    public const string Key = "imports.file_storage";
    public const string Database = "database";
    public const string File = "file";
    public const int DefaultArchiveAfterDays = 90;

    public async Task<ImportFileStorageSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        var policy = stored is null ? Default : Deserialise(stored.Value);
        return new(policy.Storage, policy.ArchiveAfterDays, stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ImportFileStorageSettings> SetAsync(UpdateImportFileStorageSettings update, CancellationToken cancellationToken)
    {
        Validate(update.Storage, update.ArchiveAfterDays);
        await settings.SetAsync(Key, JsonSerializer.Serialize(new Policy(update.Storage, update.ArchiveAfterDays), LedgerJson.Options),
            update.ExpectedVersion, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    internal async Task<Policy> PolicyAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } value ? Deserialise(value) : Default;

    private static Policy Deserialise(string json)
    {
        var policy = JsonSerializer.Deserialize<Policy>(json, LedgerJson.Options)
            ?? throw new InvalidOperationException("Stored import-file settings are invalid.");
        Validate(policy.Storage, policy.ArchiveAfterDays);
        return policy;
    }

    private static void Validate(string storage, int days)
    {
        if (storage is not (Database or File))
            throw new CommandRefusedException("import_file_storage_invalid", "Import file storage must be file or database.");
        if (days < 0 || days > 36500)
            throw new CommandRefusedException("import_file_archive_days_invalid", "Archive-after days must be between 0 and 36,500.");
    }

    internal sealed record Policy(string Storage, int ArchiveAfterDays);
    private static readonly Policy Default = new(Database, DefaultArchiveAfterDays);
}
