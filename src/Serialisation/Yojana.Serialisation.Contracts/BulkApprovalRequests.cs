namespace Yojana.Serialisation.Contracts;

// Bulk approval requests (P2-T21): a bulk preview that the requester may not commit (units blocked with
// approval_required) becomes a stored request a person holding the needed role approves or rejects. The request freezes
// what the requester saw — operation, arguments and a fingerprint of every unit's outcome — and approval executes exactly
// that, under the approver's own roles, only while the fingerprint still matches.

public static class BulkApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";

    /// <summary>The selection or the units changed after the request was raised, so the preview it froze no longer holds.</summary>
    public const string Stale = "stale";

    public static readonly IReadOnlyList<string> All = [Pending, Approved, Rejected, Withdrawn, Stale];
}

/// <summary>Raise a request from the preview the requester would get. <see cref="Requester"/> is set by the endpoint from the claims.</summary>
public sealed record RequestBulkApproval(Guid CommandId, Guid SelectionId, string Operation, BulkOperationArguments Arguments, string? Note, BulkApproval Requester);

/// <summary>Approve and execute in one transaction. <see cref="Approver"/> is set by the endpoint from the claims.</summary>
public sealed record ApproveBulkRequest(Guid CommandId, Guid RequestId, string? Note, BulkApproval Approver);

/// <summary>Reject (someone who could approve) or withdraw (the requester). A note is required.</summary>
public sealed record RejectBulkRequest(Guid RequestId, string Note, BulkApproval Decider);

public sealed record BulkApprovalRequestRow(
    Guid RequestId,
    Guid SelectionId,
    string Operation,
    string Status,
    DocumentRef SourceDocument,
    BulkOperationArguments Arguments,
    int Total,
    int Change,
    int Skip,
    int Block,
    IReadOnlyList<string> Rules,
    string RequestedBy,
    DateTime RequestedAt,
    string? Note,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? DecisionNote,
    Guid? ExecutedCommandId);

public sealed record BulkApprovalRequestPage(IReadOnlyList<BulkApprovalRequestRow> Items, int Total, int Skip, int Take);
