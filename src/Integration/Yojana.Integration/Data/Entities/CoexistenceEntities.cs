using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>
/// The last accepted remote value of one module-owned field on one remote record. A scan compares the remote value
/// with this baseline; a difference the remote cannot explain by a document becomes a conflict, never an update.
/// </summary>
public sealed class CoexistenceBaseline : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string RecordType { get; set; }
    public required string RecordKey { get; set; }
    public required string FieldName { get; set; }
    public string? Value { get; set; }
    public required string ExplainingHash { get; set; }
    public required string Source { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A remote edit to a module-owned field, parked for a person to decide. Never resolved automatically.</summary>
public sealed class CoexistenceConflict : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConflictId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string RecordType { get; set; }
    public required string RecordKey { get; set; }
    public required string RecordLabel { get; set; }
    public required string FieldName { get; set; }
    public string? BaselineValue { get; set; }
    public string? RemoteValue { get; set; }
    public required string Status { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? Decision { get; set; }
    public string? DecisionNote { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? ManualTaskId { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public static class CoexistenceConflictStatus
{
    public const string Open = "open";
    public const string AwaitingRemoteRevert = "awaiting_remote_revert";
    public const string Closed = "closed";
}

public static class CoexistenceConflictDecision
{
    /// <summary>The module value stands; someone reverts the remote edit, and the next scan closes the conflict.</summary>
    public const string KeepModule = "keep_module";
    /// <summary>The remote value is accepted as the new baseline; any module change is made in the module by a person.</summary>
    public const string AcceptRemote = "accept_remote";
    public const string Dismiss = "dismiss";
}

/// <summary>Resumable drift scan position per connection and record type.</summary>
public sealed class CoexistenceScanState : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string RecordType { get; set; }
    public string? Cursor { get; set; }
    public DateTime? PassStartedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public long LastPassRecords { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A parallel run of the module beside the incumbent remote system, started by a person.</summary>
public sealed class ParallelRunPlan : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public int Days { get; set; }
    public DateOnly StartDate { get; set; }
    public required string StartedBy { get; set; }
    public DateTime StartedAt { get; set; }
}

/// <summary>An operator's confirmation of one manual parallel-run checklist item, with evidence.</summary>
public sealed class ParallelRunConfirmation : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ItemCode { get; set; }
    public required string Evidence { get; set; }
    public required string ConfirmedBy { get; set; }
    public DateTime ConfirmedAt { get; set; }
}
