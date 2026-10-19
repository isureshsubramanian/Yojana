using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Serialisation.Imports;

public static partial class ImportFileArchiveConfiguration
{
    public static void Validate(ImportFileStorageOptions options, string operationalConnectionString)
    {
        if (options.ArchiveStores.Count == 0)
            throw Failure("import_file_archive_store_not_configured", "At least one Serialisation:ImportFiles:ArchiveStores entry is required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archiveDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in options.ArchiveStores)
        {
            if (!ArchiveName().IsMatch(store.Name) || !names.Add(store.Name))
                throw Failure("import_file_archive_store_name_invalid", $"Archive store name '{store.Name}' must be unique and contain only letters, numbers, hyphens or underscores.");
            if (store.Kind is not ("folder" or "database"))
                throw Failure("import_file_archive_store_kind_invalid", $"Archive store '{store.Name}' must be a folder or database.");
            if (store.Kind == "folder")
            {
                if (string.IsNullOrWhiteSpace(store.Path))
                    throw Failure("import_file_archive_folder_not_configured", $"Archive store '{store.Name}' needs a path.");
                if (!string.IsNullOrWhiteSpace(options.FileRoot) && PathsOverlap(options.FileRoot, store.Path))
                    throw Failure("import_file_archive_folder_overlaps_active_store", $"Archive store '{store.Name}' is the same as, contains, or is contained by the active import-file folder.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(store.ConnectionString))
                    throw Failure("import_file_archive_database_not_configured", $"Archive store '{store.Name}' needs a connection string.");
                var identity = DatabaseIdentity(store.ConnectionString);
                if (StringComparer.OrdinalIgnoreCase.Equals(identity, DatabaseIdentity(operationalConnectionString)))
                    throw Failure("import_file_archive_database_is_operational_database", $"Archive store '{store.Name}' points at the operational database.");
                if (StringComparer.OrdinalIgnoreCase.Equals(identity, DatabaseIdentity(options.DatabaseConnectionString!)))
                    throw Failure("import_file_archive_database_is_active_store", $"Archive store '{store.Name}' points at the active import-files database.");
                if (!archiveDatabases.Add(identity))
                    throw Failure("import_file_archive_database_reused", $"Archive store '{store.Name}' shares a database with another archive store.");
            }
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return StringComparer.Ordinal.Equals(first, second) || IsBelow(first, second) || IsBelow(second, first);
    }

    private static bool IsBelow(string candidate, string parent) => candidate.StartsWith(parent + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string DatabaseIdentity(string connectionString)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        return $"{connection.DataSource.Trim()}|{connection.InitialCatalog.Trim()}";
    }
    private static InvalidOperationException Failure(string reason, string detail) => new($"{reason}: {detail}");
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ArchiveName();
}

/// <summary>Migrates every configured archive database through the platform's migration step (not on every start).</summary>
public sealed class ImportFileArchiveMigrator(ImportFileStorageOptions options, ILogger<ImportFileArchiveMigrator> logger)
    : IAdditionalDatabaseMigration
{
    public string Name => "import-file archive databases";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        foreach (var store in options.ArchiveStores.Where(x => x.Kind == "database"))
        {
            logger.LogInformation("Migrating import-file archive store {ArchiveStore}", store.Name);
            var dbOptions = new DbContextOptionsBuilder<ImportFilesDbContext>().UseSqlServer(store.ConnectionString!, sql =>
                sql.MigrationsHistoryTable("__migrations", ImportFilesDbContext.SchemaName)).Options;
            await using var db = new ImportFilesDbContext(dbOptions, NoTenant.Instance);
            db.Database.SetCommandTimeout(TimeSpan.FromHours(4));
            await db.Database.MigrateAsync(cancellationToken);
        }
    }

    private sealed class NoTenant : ITenantContext
    {
        public static readonly NoTenant Instance = new();
        public bool HasTenant => false;
        public TenantId TenantId => default;
    }
}

