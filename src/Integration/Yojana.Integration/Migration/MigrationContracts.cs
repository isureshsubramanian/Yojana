namespace Yojana.Integration.LegacyMigration;

public sealed record CreateMigrationSourceRequest(
    string DisplayLabel,
    string SourceSystem,
    string Runtime,
    string Route,
    string DatabaseName,
    string SchemaName = "dbo",
    string? LinkedServerName = null,
    int CommandTimeoutSeconds = 120,
    string? ConnectionString = null,
    string? LicenceContractReference = null,
    string? LicenceNote = null);

public sealed record UpdateMigrationSourceRequest(
    Guid SourceId,
    string DisplayLabel,
    string SourceSystem,
    string Runtime,
    string Route,
    string DatabaseName,
    string SchemaName = "dbo",
    string? LinkedServerName = null,
    int CommandTimeoutSeconds = 120,
    string? ConnectionString = null);

public sealed record MigrationSourceView(
    Guid SourceId,
    string DisplayLabel,
    string SourceSystem,
    string Runtime,
    string Route,
    string DatabaseName,
    string SchemaName,
    string? LinkedServerName,
    int CommandTimeoutSeconds,
    bool HasConnectionString,
    string LicencePosition,
    string? LicenceConfirmedByName,
    DateTime? LicenceConfirmedAt,
    string? LicenceContractReference,
    string? LicenceNote,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MigrationSourcePage(IReadOnlyList<MigrationSourceView> Items, string? NextCursor);

public sealed record UpdateMigrationLicenceRequest(bool Confirmed, string? ContractReference, string? Note);

public sealed record MigrationSourceTestTable(string Table, long? RowCount, string? Error,
    string Role = "unknown", string? Label = null);

public sealed record MigrationSourceTestResult(Guid SourceId, IReadOnlyList<MigrationSourceTestTable> Tables);

public sealed record StartMigrationBatchRequest(Guid SourceId);

public sealed record StartMigrationBatchResponse(Guid BatchId, string Status, string? Failure);

// Table is the source's own name, shown as data under Label; Role is the stable key screens select on.
public sealed record MigrationBatchTableView(
    string Table,
    string Role,
    string Label,
    long RowsRead,
    long Inserted,
    long Updated,
    long Unchanged,
    long Absent,
    long DurationMilliseconds);

public sealed record MigrationBatchView(
    Guid BatchId,
    Guid SourceId,
    string SourceSystem,
    string Kind,
    string RequestedByName,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string Status,
    string? Failure,
    IReadOnlyList<MigrationBatchTableView>? Tables = null);

public sealed record MigrationBatchPage(IReadOnlyList<MigrationBatchView> Items, string? NextCursor);

public sealed record MigrationProfileClassView(
    string Code,
    string Name,
    string Outcome,
    string OutcomeLabel,
    long? Count,
    long? Change,
    string? Note);

public sealed record MigrationProfileView(
    Guid RunId,
    Guid BatchId,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string Status,
    IReadOnlyList<MigrationProfileClassView> Classes);

public sealed record StartMigrationProfileResponse(Guid RunId, Guid BatchId, string Status);

public sealed record MigrationProfileSample(
    string SourceKey,
    string? Serial,
    string? Item,
    IReadOnlyDictionary<string, string?> Facts);

public sealed record MigrationProfileSamplesView(
    Guid RunId,
    string ClassCode,
    IReadOnlyList<MigrationProfileSample> Samples);

public sealed record MigrationProfileOutcomeChoice(string ClassCode, string Outcome);

public sealed record MigrationProfileOutcomeSettingsView(
    IReadOnlyList<MigrationProfileOutcomeChoice> Classes,
    string Version);

public sealed record UpdateMigrationProfileOutcomeSettings(
    IReadOnlyList<MigrationProfileOutcomeChoice> Classes,
    string ExpectedVersion);

public sealed class MigrationRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}
