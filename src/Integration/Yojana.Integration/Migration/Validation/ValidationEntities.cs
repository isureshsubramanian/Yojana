using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Validation;

public static class ValidationTargetKind
{
    public const string Plan = "plan";
    public const string Module = "module";
}

public static class ValidationRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class ValidationResult
{
    public const string Pass = "pass";
    public const string Explained = "explained";
    public const string Fail = "fail";
    public const string HardFail = "hard_fail";
    public const string Report = "report";
    public const string Warning = "warning";
}

public static class ValidationSignatureRole
{
    public const string Preparer = "preparer";
    public const string Reviewer = "reviewer";
}

public sealed class MigrationValidationRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid BatchId { get; set; }
    public Guid MapRunId { get; set; }
    public required string TargetKind { get; set; }
    public bool Rehearsal { get; set; }
    public int SampleSeed { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public required string Status { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }
}

public sealed class MigrationValidationCheck : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CheckId { get; set; }
    public Guid RunId { get; set; }
    public required string Code { get; set; }
    public required string Label { get; set; }
    public required string LimitKind { get; set; }
    public long? SourceTotal { get; set; }
    public long? TargetTotal { get; set; }
    public decimal? Difference { get; set; }
    public required string Result { get; set; }
    public string? OriginalResult { get; set; }
    public string? Explanation { get; set; }
    public required string HowCounted { get; set; }
    public string ResultFingerprint { get; set; } = string.Empty;
    public DateTime RanAt { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? ExplainedBy { get; set; }
    public DateTime? ExplainedAt { get; set; }
}

public sealed class MigrationValidationCheckPart : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PartId { get; set; }
    public Guid CheckId { get; set; }
    public required string Code { get; set; }
    public required string Label { get; set; }
    public decimal Value { get; set; }
}

public sealed class MigrationValidationDifference : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid DifferenceId { get; set; }
    public Guid CheckId { get; set; }
    public required string Kind { get; set; }
    public string? ItemCode { get; set; }
    public string? WarehouseCode { get; set; }
    public string? Serial { get; set; }
    public decimal SourceValue { get; set; }
    public decimal TargetValue { get; set; }
    public decimal Difference { get; set; }
    public string? Detail { get; set; }
}

public sealed class MigrationValidationSample : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid SampleId { get; set; }
    public Guid RunId { get; set; }
    public Guid CheckId { get; set; }
    public Guid PlanUnitId { get; set; }
    public required string Serial { get; set; }
    public required string ItemCode { get; set; }
    public required string ItemFamily { get; set; }
    public required string AgeBand { get; set; }
    public required string SourceTrailJson { get; set; }
    public required string TargetTrailJson { get; set; }
    public string? Outcome { get; set; }
    public string? Note { get; set; }
    public string? TracedBy { get; set; }
    public DateTime? TracedAt { get; set; }
}

public sealed class MigrationValidationSignature : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid SignatureId { get; set; }
    public Guid CheckId { get; set; }
    public required string Role { get; set; }
    public required string Actor { get; set; }
    public DateTime SignedAt { get; set; }
    public bool Superseded { get; set; }
    public DateTime? SupersededAt { get; set; }
    public Guid? CarriedFromSignatureId { get; set; }
}

public sealed class MigrationSignoffRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RecordId { get; set; }
    public Guid RunId { get; set; }
    public bool Rehearsal { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

public sealed class MigrationSignoffLine : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid LineId { get; set; }
    public Guid RecordId { get; set; }
    public int Position { get; set; }
    public required string ReportName { get; set; }
    public long? SourceTotal { get; set; }
    public long? TargetTotal { get; set; }
    public decimal? Difference { get; set; }
    public string? Explanation { get; set; }
    public required string Result { get; set; }
    public required string Preparer { get; set; }
    public DateTime PreparedAt { get; set; }
    public required string Reviewer { get; set; }
    public DateTime ReviewedAt { get; set; }
}
