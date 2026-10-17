using System.Text.Json;

namespace Yojana.Serialisation.Contracts;

public static class WorkbenchTargetKind
{
    public const string Identifier = "identifier";
    public const string Unit = "unit";
    public const string DocumentLine = "document_line";
    public const string SelectionSet = "selection_set";
}

/// <summary>One of the P1 lookup and selection-set entry points understood by the correction workbench.</summary>
public sealed record WorkbenchEntry(
    string Kind,
    string? Value = null,
    string? IdentifierType = null,
    Guid? UnitId = null,
    DocumentRef? Document = null,
    Guid? SelectionId = null);

public sealed record ResolveWorkbenchEntry(Guid ResolutionId, WorkbenchEntry Entry, string ActorRef);

public sealed record WorkbenchCandidate(
    Guid UnitId,
    Guid ResolvedUnitId,
    bool MergedAlias,
    string LifecycleStatus,
    Guid? ItemId,
    string? DisplayIdentifier,
    string? MatchKind = null);

public sealed record WorkbenchResolution(
    WorkbenchEntry Entry,
    IReadOnlyList<WorkbenchCandidate> Candidates,
    Guid? SelectionId = null,
    string? Resolution = null);

/// <summary>A typed narrowing option. Selecting it fixes the existing command and its default reason.</summary>
public sealed record WorkbenchChoiceOption(string Value, string Label, string Command, string DefaultReason, int RootCause);

public sealed record WorkbenchNarrowingQuestion(string Key, string Prompt, IReadOnlyList<WorkbenchChoiceOption> Options);

public sealed record WorkbenchChoice(
    string Choice,
    string Label,
    string Area,
    string HelpText,
    IReadOnlyList<string> Commands,
    IReadOnlyList<WorkbenchNarrowingQuestion> Questions,
    string DefaultReason,
    int RootCause);

/// <summary>The chosen route plus the existing command's arguments. Server-owned identity fields are overwritten.</summary>
public sealed record WorkbenchAnswers(string Command, JsonElement Arguments, IReadOnlyDictionary<string, string>? Narrowing = null);

public sealed record WorkbenchEvidenceRef(string Kind, string Ref, string? Note = null);

public sealed record WorkbenchTarget(string Kind, Guid? UnitId = null, DocumentRef? Document = null, Guid? SelectionId = null);

/// <summary>The stable input whose command preview is shown to both initiator and approvers.</summary>
public sealed record WorkbenchPreviewRequest(
    Guid CommandId,
    string Choice,
    WorkbenchAnswers Answers,
    WorkbenchTarget Target,
    string Reason,
    string? Note = null,
    IReadOnlyList<WorkbenchEvidenceRef>? Evidence = null);

public sealed record WorkbenchPreviewResult(string Choice, string Command, CorrectionPreview Preview, string PreviewHash);

public static class WorkbenchRequestStatus
{
    public const string Draft = "draft";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Committed = "committed";
    public const string Superseded = "superseded";

    public static readonly IReadOnlyList<string> All = [Draft, AwaitingApproval, Approved, Rejected, Committed, Superseded];
}

public sealed record WorkbenchActor(string UserRef, IReadOnlyList<string> Roles);

public sealed record WorkbenchApproval(string UserRef, IReadOnlyList<string> Roles, DateTime ApprovedAt, string? Note = null);

public sealed record WorkbenchRequestRow(
    Guid RequestId,
    Guid CommandId,
    string Choice,
    string Command,
    WorkbenchTarget Target,
    WorkbenchAnswers Answers,
    string Reason,
    string? Note,
    IReadOnlyList<WorkbenchEvidenceRef> Evidence,
    CorrectionPreview Preview,
    string PreviewHash,
    string Status,
    string InitiatedBy,
    IReadOnlyList<string> InitiatorRoles,
    DateTime InitiatedAt,
    IReadOnlyList<string> RequiredApproverRoles,
    IReadOnlyList<WorkbenchApproval> Approvals,
    string? RejectedBy = null,
    DateTime? RejectedAt = null,
    string? DecisionNote = null,
    string? CommittedBy = null,
    DateTime? CommittedAt = null,
    JsonElement? Result = null);

public sealed record WorkbenchRequestPage(IReadOnlyList<WorkbenchRequestRow> Items, int Total, int Skip, int Take);

public sealed record RejectWorkbenchRequest(string Note);

public sealed record ApproveWorkbenchRequest(string? Note = null);

public sealed record WorkbenchItemAssignment(Guid AssignmentId, Guid ItemId, Guid? ModelVersionId, DateTime ValidFrom, DateTime? ValidTo, string Reason);

public sealed record WorkbenchEntitlement(
    Guid EntitlementId,
    string CoverageFamily,
    string Type,
    string Direction,
    string Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    Guid? SupersedesEntitlementId,
    Guid? ParentEntitlementId);

public sealed record WorkbenchAssemblyLink(
    Guid LinkId,
    Guid ParentUnitId,
    Guid? ChildUnitId,
    Guid? ChildItemId,
    string Relation,
    string? Slot,
    DateTime InstalledAt,
    DateTime? RemovedAt,
    string Perspective);

public sealed record WorkbenchMergeHistory(Guid CorrectionId, Guid SurvivorUnitId, Guid AbsorbedUnitId, DateTime MergedAt, Guid? ReversedByCorrectionId, DateTime? ReversedAt);

public sealed record WorkbenchCorrectionHistory(
    Guid CorrectionId,
    string Command,
    string ReasonCode,
    string InitiatedVia,
    string InitiatingRole,
    string ActorRef,
    DateTime EventTime,
    DateTime RecordedTime,
    Guid? CorrectsCorrectionId,
    string? Note);

public sealed record WorkbenchUnitLineage(
    Guid UnitId,
    UnitDetail State,
    IReadOnlyList<IdentifierRow> Identifiers,
    IReadOnlyList<WorkbenchItemAssignment> ItemAssignments,
    IReadOnlyList<EpisodeRow> Episodes,
    IReadOnlyList<WorkbenchEntitlement> Entitlements,
    IReadOnlyList<WorkbenchAssemblyLink> Assembly,
    IReadOnlyList<WorkbenchMergeHistory> Merges,
    IReadOnlyList<WorkbenchCorrectionHistory> Corrections,
    IReadOnlyList<HistoryRow> LastEvents);
