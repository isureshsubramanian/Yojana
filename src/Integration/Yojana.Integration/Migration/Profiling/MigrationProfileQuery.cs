using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Profiling;

public sealed record MigrationProfileQueryContext(
    IntegrationDbContext Db,
    TenantId TenantId,
    Guid BatchId,
    string SourceSystem,
    DateTime ExtractionDate);

public sealed record MigrationProfileQueryAnswer(
    long? Count,
    string? Note,
    IReadOnlyList<MigrationProfileSample> Samples);

public interface IMigrationProfileQuery
{
    MigrationProfileClass Class { get; }
    Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken);
}
