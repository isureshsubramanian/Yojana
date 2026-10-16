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

    public string? Serial { get; set; }

    /// <summary>Outcome of the last preview or execution (change, skip, block, done, failed), null before any preview.</summary>
    public string? Outcome { get; set; }

    public string? Reason { get; set; }

    public string? Detail { get; set; }
}
