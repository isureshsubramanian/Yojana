using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Data.Platform.Entities;

public sealed class ErasureRequest : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErasureId { get; set; }
    public ErasureSubjectKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    public required string RequestReference { get; set; }
    public required string Reason { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public required string Status { get; set; }
    public required string RetentionBasis { get; set; }
    public string ReviewDecisionsJson { get; set; } = "[]";
    public string? FailureRule { get; set; }
    public string? FailureDetail { get; set; }
}

public sealed class ErasureStoreCount : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErasureId { get; set; }
    public required string Store { get; set; }
    public required string Kind { get; set; }
    public int PreviewCount { get; set; }
    public int ErasedCount { get; set; }
    public required string Action { get; set; }
    public required string Status { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class ErasureRequestStatus
{
    // TIGHT COUPLING (ADR-0017 certificate redaction): the Serialisation certificate trigger reads
    // platform.erasure_request and admits its narrow update only for subject_kind 'Party' in pending/running state.
    // Renaming the table, enum strings or these status values without changing the trigger makes lawful redaction
    // fail closed. The matching trigger comment is in Erase01CertificateRedaction.
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class ErasureStoreStatus
{
    public const string Previewed = "previewed";
    public const string Completed = "completed";
}
