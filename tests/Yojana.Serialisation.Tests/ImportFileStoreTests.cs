using System.Text;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Imports;

namespace Yojana.Serialisation.Tests;

public sealed class ImportFileStoreTests
{
    private const string Operational = "Server=fictional;Database=operations;User Id=sa;Password=x";
    [Fact]
    public async Task File_backend_round_trips_deletes_and_refuses_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "yojana-file-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tenant = new TestTenant(new TenantId(Guid.NewGuid()));
            var backend = new FileImportFileBackend(new ImportFileStorageOptions { FileRoot = root }, tenant);
            var bytes = Encoding.UTF8.GetBytes("Serial\r\nFIC-001\r\n");
            await using var input = new MemoryStream(bytes);
            var stored = await backend.PutAsync(Guid.NewGuid(), "fictional.csv", ImportFileCopyKind.Original,
                input, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), CancellationToken.None);

            Assert.Equal(bytes.Length, stored.ContentLength);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories));
            await using (var opened = await backend.OpenReadAsync(stored.Reference, CancellationToken.None))
            {
                using var copy = new MemoryStream();
                await opened.Content.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
            }

            var escape = "file:v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.Combine(tenant.TenantId.Value.ToString("N"), "..", "other", "secret.csv")));
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => backend.OpenReadAsync(escape, CancellationToken.None));
            Assert.Equal("import_file_path_invalid", refusal.Rule);

            var other = new FileImportFileBackend(new ImportFileStorageOptions { FileRoot = root },
                new TestTenant(new TenantId(Guid.NewGuid())));
            var isolation = await Assert.ThrowsAsync<CommandRefusedException>(() => other.OpenReadAsync(stored.Reference, CancellationToken.None));
            Assert.Equal("import_file_path_invalid", isolation.Rule);
            await backend.DeleteAsync(stored.Reference, CancellationToken.None);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Archive_configuration_refuses_active_database_and_overlapping_folder()
    {
        var sameDatabase = new ImportFileStorageOptions
        {
            DatabaseConnectionString = "Server=fictional;Database=files;User Id=sa;Password=x",
            FileRoot = Path.Combine(Path.GetTempPath(), "active-files"),
            ArchiveStores = [new() { Name = "old", Kind = "database", ConnectionString = Operational }],
        };
        var database = Assert.Throws<InvalidOperationException>(() => ImportFileArchiveConfiguration.Validate(sameDatabase, Operational));
        Assert.StartsWith("import_file_archive_database_is_operational_database", database.Message);

        sameDatabase.ArchiveStores[0].ConnectionString = sameDatabase.DatabaseConnectionString;
        var activeDatabase = Assert.Throws<InvalidOperationException>(() => ImportFileArchiveConfiguration.Validate(sameDatabase, Operational));
        Assert.StartsWith("import_file_archive_database_is_active_store", activeDatabase.Message);

        var nested = new ImportFileStorageOptions
        {
            DatabaseConnectionString = "Server=fictional;Database=files;User Id=sa;Password=x",
            FileRoot = Path.Combine(Path.GetTempPath(), "active-files"),
            ArchiveStores = [new() { Name = "old", Kind = "folder", Path = Path.Combine(Path.GetTempPath(), "active-files", "archive") }],
        };
        var folder = Assert.Throws<InvalidOperationException>(() => ImportFileArchiveConfiguration.Validate(nested, Operational));
        Assert.StartsWith("import_file_archive_folder_overlaps_active_store", folder.Message);
    }

    [Theory]
    [InlineData("months", "2026-03-31", 1, "2026-02-28")]
    [InlineData("years", "2024-02-29", 1, "2023-02-28")]
    public void Archive_age_uses_calendar_arithmetic(string unit, string now, int age, string expected)
    {
        var policy = new TenantImportFileSettings.Policy("database", "old", age, unit);
        Assert.Equal(DateTime.Parse(expected).Date, policy.Cutoff(DateTime.Parse(now)).Date);
    }

    [Fact]
    public async Task Existing_day_setting_reads_as_the_new_day_based_shape()
    {
        var legacy = new LegacySettings("{\"storage\":\"file\",\"archive_after_days\":730}");
        var options = new ImportFileStorageOptions
        {
            ArchiveStores = [new() { Name = "long-term", Kind = "folder", Path = "/fictional/archive" }],
        };
        var settings = await new TenantImportFileSettings(legacy, options).GetAsync(CancellationToken.None);
        Assert.Equal(("file", "long-term", 730, "days"),
            (settings.Storage, settings.ArchiveStore, settings.ArchiveAge, settings.ArchiveAgeUnit));
    }

    [Fact]
    public async Task A_tenant_that_never_chose_archives_after_two_years()
    {
        var options = new ImportFileStorageOptions
        {
            ArchiveStores = [new() { Name = "long-term", Kind = "folder", Path = "/fictional/archive" }],
        };
        var settings = await new TenantImportFileSettings(new LegacySettings(null), options).GetAsync(CancellationToken.None);
        Assert.Equal(("database", "long-term", 2, "years"),
            (settings.Storage, settings.ArchiveStore, settings.ArchiveAge, settings.ArchiveAgeUnit));
    }

    private sealed class TestTenant(TenantId id) : ITenantContext
    {
        public bool HasTenant => true;
        public TenantId TenantId { get; } = id;
    }

    private sealed class LegacySettings(string? value) : ITenantSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(value);
        public Task SetAsync(string key, string next, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(value is null ? null : new VersionedTenantSetting(value, "1", DateTime.UtcNow));
    }
}
