using System.Text;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Imports;

namespace Yojana.Serialisation.Tests;

public sealed class ImportFileStoreTests
{
    [Fact]
    public async Task File_backend_round_trips_archives_atomically_and_refuses_traversal()
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

            var archived = await backend.ArchiveAsync(stored.Reference, CancellationToken.None);
            Assert.True(archived.Archived);
            await using var archivedRead = await backend.OpenReadAsync(archived.Reference, CancellationToken.None);
            Assert.True(archivedRead.Archived);

            var escape = "file:v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.Combine(tenant.TenantId.Value.ToString("N"), "..", "other", "secret.csv")));
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => backend.OpenReadAsync(escape, CancellationToken.None));
            Assert.Equal("import_file_path_invalid", refusal.Rule);

            var other = new FileImportFileBackend(new ImportFileStorageOptions { FileRoot = root },
                new TestTenant(new TenantId(Guid.NewGuid())));
            var isolation = await Assert.ThrowsAsync<CommandRefusedException>(() => other.OpenReadAsync(archived.Reference, CancellationToken.None));
            Assert.Equal("import_file_path_invalid", isolation.Rule);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestTenant(TenantId id) : ITenantContext
    {
        public bool HasTenant => true;
        public TenantId TenantId { get; } = id;
    }
}
