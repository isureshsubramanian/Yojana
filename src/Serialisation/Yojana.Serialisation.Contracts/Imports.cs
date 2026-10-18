namespace Yojana.Serialisation.Contracts;

public static class ImportSource
{
    public const string UserUpload = "user_upload";
    public const string ThreePlFeed = "three_pl_feed";
    public const string VendorAsn = "vendor_asn";
    public const string Edi = "edi";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([UserUpload, ThreePlFeed, VendorAsn, Edi], StringComparer.Ordinal);
}

public static class ImportFormat
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";
    public const string Xls = "xls";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Csv, Xlsx, Xls], StringComparer.Ordinal);
}

public static class ImportMode
{
    public const string Apply = "apply";
    public const string Compare = "compare";
}

public static class ImportOutcome
{
    public const string Applied = "applied";
    public const string Skipped = "skipped";
    public const string Rejected = "rejected";
}

public static class ImportReviewAction
{
    public const string Assign = "assign";
    public const string Skip = "skip";
    public const string Flag = "flag";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Assign, Skip, Flag], StringComparer.Ordinal);
}

public static class ImportComparisonPartition
{
    public const string OnlyA = "only_in_a";
    public const string OnlyB = "only_in_b";
    public const string Both = "both";
    public const string DuplicateInFile = "duplicate_in_file";
    public const string ExistingCollision = "existing_collision";
}

/// <summary>Header-name mapping saved per source. Values are source column names, not ordinals.</summary>
public sealed record ImportColumnMapping(
    string Serial,
    string? OrderNumber = null,
    string? UnitId = null,
    string? Lot = null,
    string? CustomerAssetTag = null,
    string? Item = null);

public sealed record ImportFile(string FileName, string Format, byte[] Content);

/// <summary>
/// Ingest and optionally apply a serial file. Actor and committer are trusted boundary fields: HTTP always replaces
/// both from claims. A single file may yield several jobs when its rows name several production orders.
/// </summary>
public sealed record RunImport(
    Guid CommandId,
    string Source,
    ImportFile File,
    DateTime EventTime,
    Guid LegalEntityId,
    string ActorRef,
    DocumentRef? Target = null,
    ImportColumnMapping? Mapping = null,
    string MappingName = "default",
    string Mode = ImportMode.Apply,
    CorrectionCommitter? CommittedBy = null);

public sealed record ImportRowView(
    Guid RowResultId,
    int RowNumber,
    string? Sheet,
    string? RawValue,
    string? NormalisedValue,
    string Outcome,
    string? Rule,
    string? OrderNumber,
    string? Lot,
    string? CustomerAssetTag,
    Guid? UnitId,
    string? ComparisonPartition = null,
    string? ProposedAction = null,
    string? ReviewedAction = null,
    string? CollisionUnitName = null,
    string? CollisionOwnerName = null);

public sealed record ImportJobView(
    Guid JobId,
    string Source,
    string FileName,
    string OriginalHash,
    string? ConvertedHash,
    string Format,
    string Mode,
    DocumentRef? Target,
    string? TargetName,
    string Status,
    int Applied,
    int Skipped,
    int Rejected,
    string? OriginalArchiveReference,
    string? ConvertedArchiveReference,
    string? InboxReference,
    DateTime RecordedTime,
    IReadOnlyList<ImportRowView> Rows);

public sealed record RunImportResult(IReadOnlyList<ImportJobView> Jobs, bool Replayed);

public sealed record ImportJobSummary(Guid JobId, string Source, string FileName, string Format, string Mode, string Status,
    int Applied, int Skipped, int Rejected, DateTime RecordedTime);
public sealed record ImportJobPage(IReadOnlyList<ImportJobSummary> Items, string? NextCursor);
public sealed record ImportRowPage(IReadOnlyList<ImportRowView> Items, string? NextCursor);

/// <summary>Compare two already-intaken jobs, or one job against a stored selection set.</summary>
public sealed record CompareImports(Guid CommandId, Guid JobAId, Guid? JobBId, Guid? SelectionId, string ActorRef);

public sealed record CompareImportsResult(Guid ComparisonJobId, int OnlyInA, int OnlyInB, int Both, int Duplicates, int ExistingCollisions, IReadOnlyList<ImportRowView> Rows);

public sealed record ImportReviewOverride(Guid RowResultId, string Action);

/// <summary>The reviewed compare result is the only compare-mode input handed to AssignIdentifiers.</summary>
public sealed record ApplyImportReview(Guid CommandId, Guid JobId, IReadOnlyList<ImportReviewOverride> Overrides,
    DateTime EventTime, Guid LegalEntityId, string ActorRef, CorrectionCommitter? CommittedBy = null);

public sealed record ApplyImportReviewResult(Guid JobId, int Assigned, int Skipped, int Flagged, IReadOnlyList<ImportRowView> Rows, bool Replayed);

public sealed record ImportJobSourceRow(Guid? UnitId, string RawValue, string? NormalisedValue, DocumentRef? Target, string Outcome, string? Rule);

/// <summary>Narrow MOD-12 read seam consumed by selection sets, corrections and later portal reconciliation.</summary>
public interface IImportJobSource
{
    Task<IReadOnlyList<ImportJobSourceRow>> ReadAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Every validator-accepted source row for customer reconciliation. Unlike selection/correction consumption,
    /// compare-mode review actions do not suppress a customer's claim before reconciliation classifies it.
    /// </summary>
    Task<IReadOnlyList<ImportJobSourceRow>> ReadForReconciliationAsync(Guid jobId,
        CancellationToken cancellationToken);
}

public sealed record SpreadsheetCell(string? Text, bool IsNumeric = false, bool IsFormula = false);
public sealed record SpreadsheetRow(string Sheet, int RowNumber, IReadOnlyList<SpreadsheetCell> Cells);
public sealed record SpreadsheetReadResult(IReadOnlyList<SpreadsheetRow> Rows, byte[]? ConvertedXlsx = null);

/// <summary>Format adapter. The intake service owns mapping, validation, hashes and persistence.</summary>
public interface ISpreadsheetReader
{
    bool CanRead(string format);
    Task<SpreadsheetReadResult> ReadAsync(Stream input, CancellationToken cancellationToken);
}

/// <summary>Inbox/archive storage boundary. Implementations return opaque references only.</summary>
public interface IImportFileStore
{
    Task<string> RetainInboxAsync(Guid jobId, string fileName, byte[] content, CancellationToken cancellationToken);
    Task<string> ArchiveAsync(Guid jobId, string fileName, byte[] content, string copyKind, CancellationToken cancellationToken);
}

public sealed record TabularExport(IReadOnlyList<string> Headers, IAsyncEnumerable<IReadOnlyList<string?>> Rows);

/// <summary>One shared streaming CSV/XLSX writer used by every list endpoint.</summary>
public interface ITabularExporter
{
    string ContentType(string format);
    string Extension(string format);
    Task WriteAsync(string format, TabularExport table, Stream output, CancellationToken cancellationToken);
}

public sealed record ThreePlFeedUnit(string Serial, string? Lot = null, string? CustomerAssetTag = null);

/// <summary>Case 24/T58: confirm a pending shipment line and add any captured customer asset tags.</summary>
public sealed record ReceiveThreePlFeed(Guid CommandId, string MessageReference, DocumentRef Shipment, int Line,
    DateTime PhysicalShipTime, IReadOnlyList<ThreePlFeedUnit> Units, string ActorRef, Guid LegalEntityId);

public sealed record ReceiveThreePlFeedResult(Guid FeedMessageId, Guid ImportJobId, int Shipped, int AssetTagsAssigned,
    int Rejected, IReadOnlyList<string> Exceptions, bool Replayed = false);

public sealed record ImportCompleted(PublishedSummaryHeader Header, Guid JobId, string Source, string OriginalHash,
    string? ConvertedHash, string Status, int Applied, int Skipped, int Rejected) : IPublishedSummaryEvent
{
    public const string Schema = "Import.Completed";
    public const string Version = "1.0";
}
