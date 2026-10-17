using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>A resolved selection: the source as JSON and the resolved count; members hold the unit ids and the last outcome per unit (the validation report).</summary>
public sealed class SelectionSet : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid SelectionId { get; set; }

    public required string SourceKind { get; set; }

    public required string SourceJson { get; set; }

    public int ResolvedCount { get; set; }

    public int UnknownCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    /// <summary>Set once executed: the executing command id and the operation, so a replay returns the same report.</summary>
    public Guid? ExecutedCommandId { get; set; }

    public string? ExecutedOperation { get; set; }

    public DateTime? ExecutedAt { get; set; }

    public string? SourceDocumentType { get; set; }

    public Guid? SourceDocumentId { get; set; }

    public string? ApproverRef { get; set; }
}

public sealed class SelectionSetMember : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid SelectionId { get; set; }

    public Guid UnitId { get; set; }

    public Guid? MergedFromUnitId { get; set; }

    public string? Serial { get; set; }

    /// <summary>Outcome of the last preview or execution (change, skip, block, done, failed), null before any preview.</summary>
    public string? Outcome { get; set; }

    public string? Reason { get; set; }

    public string? Detail { get; set; }
}

/// <summary>
/// P2-T21: a bulk execution waiting for someone with the role its preview demanded. <see cref="Fingerprint"/> hashes every
/// unit's outcome as the requester saw it; approval re-runs that preview and executes only while the hash still matches.
/// </summary>
public sealed class BulkApprovalRequest : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>The raising command, so a retried request returns the same row.</summary>
    public Guid CommandId { get; set; }

    public Guid SelectionId { get; set; }

    public required string Operation { get; set; }

    /// <summary>The <c>BulkOperationArguments</c> as JSON, reserved <c>__</c> keys removed.</summary>
    public required string ArgumentsJson { get; set; }

    public required string SourceDocumentType { get; set; }

    public Guid SourceDocumentId { get; set; }

    public required string Status { get; set; }

    public required string Fingerprint { get; set; }

    public int Total { get; set; }

    public int ChangeCount { get; set; }

    public int SkipCount { get; set; }

    public int BlockCount { get; set; }

    /// <summary>The distinct refusal rules of the blocked units, comma separated.</summary>
    public required string Rules { get; set; }

    public required string RequestedBy { get; set; }

    /// <summary>The requester's roles when raised, comma separated; the staleness check re-runs the preview with them.</summary>
    public required string RequestedRoles { get; set; }

    public DateTime RequestedAt { get; set; }

    public string? Note { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecisionNote { get; set; }

    public Guid? ExecutedCommandId { get; set; }
}
