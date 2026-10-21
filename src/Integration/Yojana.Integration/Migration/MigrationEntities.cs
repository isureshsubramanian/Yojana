using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration;

public static class MigrationSourceRuntime
{
    public const string SqlServer = "sql_server";
    public static readonly string UnsupportedColumnar = string.Concat('h', 'a', 'n', 'a');
}

public static class MigrationSourceRoute
{
    public const string Separate = "separate";
    public const string CrossDatabase = "cross_database";
    public const string LinkedServer = "linked_server";
}

public static class MigrationLicencePosition
{
    public const string Unconfirmed = "unconfirmed";
    public const string Confirmed = "confirmed";
}

public static class MigrationBatchStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Refused = "refused";
}

public sealed class MigrationSource : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string DisplayLabel { get; set; }
    public required string SourceSystem { get; set; }
    public required string Runtime { get; set; }
    public required string Route { get; set; }
    public required string DatabaseName { get; set; }
    public required string SchemaName { get; set; }
    public string? LinkedServerName { get; set; }
    public int CommandTimeoutSeconds { get; set; }
    public required string LicencePosition { get; set; }
    public string? LicenceConfirmedBy { get; set; }
    public DateTime? LicenceConfirmedAt { get; set; }
    public string? LicenceContractReference { get; set; }
    public string? LicenceNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class MigrationBatch : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid BatchId { get; set; }
    public required string SourceSystem { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Kind { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public required string Status { get; set; }
    public string? FailureText { get; set; }
}

public sealed class MigrationBatchTable : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid BatchId { get; set; }
    public required string SourceTable { get; set; }
    public long RowsRead { get; set; }
    public long Inserted { get; set; }
    public long Updated { get; set; }
    public long Unchanged { get; set; }
    public long Absent { get; set; }
    public long DurationMilliseconds { get; set; }
}
