namespace Yojana.Integration.LegacyMigration.Validation;

public sealed record StartValidationRequest(bool Rehearsal);
public sealed record StartValidationResponse(Guid RunId, Guid BatchId, string Status, string TargetKind);
public sealed record ValidationSignatureView(string Role, string ActorName, DateTime SignedAt,
    bool Superseded, DateTime? SupersededAt);
public sealed record ValidationCheckPartView(string Code, string Label, decimal Value);
public sealed record ValidationCheckView(Guid CheckId, string Code, string Label, string LimitKind,
    long? SourceTotal, long? TargetTotal, decimal? Difference, string Result, string? Explanation,
    string? ExplainedByName, DateTime? ExplainedAt, string HowCounted, DateTime RanAt, long DurationMilliseconds,
    IReadOnlyList<ValidationSignatureView> Signatures);
public sealed record MigrationValidationView(Guid RunId, Guid BatchId, Guid MapRunId, string TargetKind,
    bool Rehearsal, int? RehearsalNumber, string Status, DateTime? StartedAt, DateTime? FinishedAt,
    DateTime? LoadDate, Guid? SignoffRecordId, bool? SignoffRehearsal, DateTime? SignoffCreatedAt,
    string? SignoffCreatedByName, string? FailureCode, string? FailureDetail, int ChecksTotal, int Passed,
    int Explained, int Failed, int HardFailed, int SignedChecks, long Units, long Events,
    decimal? EventsShortPercent, int SampleTraced, int SampleTotal, string? HardFailSummary,
    IReadOnlyList<ValidationCheckView> Checks);
public sealed record ValidationCheckDetail(ValidationCheckView Check,
    IReadOnlyList<ValidationCheckPartView> Parts, IReadOnlyList<ValidationSignatureView> SignatureHistory);
public sealed record ValidationDifferenceView(Guid DifferenceId, string Kind, string? ItemCode,
    string? Warehouse, string? Serial, decimal SourceValue, decimal TargetValue, decimal Difference,
    string? Detail);
public sealed record ValidationDifferencePage(IReadOnlyList<ValidationDifferenceView> Items, string? NextCursor);
public sealed record ExplainValidationCheckRequest(string Explanation);
public sealed record SignValidationCheckRequest(string Role);
public sealed record TraceValidationSampleRequest(string Outcome, string? Note);
public sealed record ValidationSampleView(Guid SampleId, string Serial, string ItemCode, string ItemFamily,
    string AgeBand, string SourceTrailJson, string TargetTrailJson, string? Outcome, string? Note,
    string? TracedByName, DateTime? TracedAt);
public sealed record ValidationSamplePage(IReadOnlyList<ValidationSampleView> Items, string? NextCursor);
public sealed record ValidationSettingsView(decimal HistoricalTolerancePercent, int SampleSize,
    IReadOnlyList<int> AgeBandYears, string Version);
public sealed record UpdateValidationSettingsRequest(decimal HistoricalTolerancePercent, int SampleSize,
    IReadOnlyList<int> AgeBandYears, string ExpectedVersion);
public sealed record SignoffLineView(int Position, string ReportName, long? SourceTotal, long? TargetTotal,
    decimal? Difference, string? Explanation, string Result, string PreparerName,
    DateTime PreparedAt, string ReviewerName, DateTime ReviewedAt);
public sealed record SignoffRecordView(Guid RecordId, Guid RunId, bool Rehearsal, DateTime CreatedAt,
    string CreatedByName, IReadOnlyList<SignoffLineView> Lines);

public sealed class ValidationRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}
