using System.Text.Json;

namespace Yojana.Serialisation.Contracts;

public static class WorkbenchTargetKind
{
    public const string Identifier = "identifier";
    public const string Unit = "unit";
    public const string DocumentLine = "document_line";
    public const string SelectionSet = "selection_set";
}

/// <summary>Routes that hand the workbench to a sibling-owned document screen instead of adapting that command inline.</summary>
public static class WorkbenchRouteCommand
{
    public const string ShipWarrantyReplacement = "replacement.ship";
    public const string ReleaseRefurbishment = "refurbishment.release";
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
    int RootCause,
    IReadOnlyList<string>? SearchWords = null,
    int Order = 0,
    IReadOnlyList<string>? InitiateRoles = null,
    IReadOnlyList<string>? CommitRoles = null,
    string Status = WorkbenchChoiceStatus.Shown,
    bool Standard = true,
    bool Fallback = false);

public static class WorkbenchChoiceStatus
{
    public const string Shown = "shown";
    public const string Retired = "retired";
    public static readonly IReadOnlyList<string> All = [Shown, Retired];
}

public static class WorkbenchChoiceId
{
    public const string Fallback = "none_or_not_sure";
}

/// <summary>The complete versioned tenant setting used by both the picker and approval workflow.</summary>
public sealed record WorkbenchChoiceConfiguration(
    int Version,
    IReadOnlyList<WorkbenchChoice> Choices,
    decimal UnclassifiedAlertPercent,
    string UpdatedBy,
    DateTime UpdatedAt);

/// <summary>Administrator input. Version and audit fields are always supplied by the server.</summary>
public sealed record UpdateWorkbenchChoices(IReadOnlyList<WorkbenchChoice> Choices, decimal UnclassifiedAlertPercent);

public sealed record WorkbenchChoiceConfigurationVersion(
    WorkbenchChoiceConfiguration Configuration,
    DateTime ValidFrom,
    DateTime ValidTo);

public sealed record WorkbenchChoiceSettingsView(
    WorkbenchChoiceConfiguration Current,
    IReadOnlyList<WorkbenchChoiceConfigurationVersion> History);

public sealed record WorkbenchChoiceGroup(string Area, IReadOnlyList<WorkbenchChoice> Choices);

public sealed record WorkbenchChoiceSuggestion(string Choice, string Rule, string Label);

public sealed record WorkbenchPicker(
    string? Search,
    IReadOnlyList<WorkbenchChoiceSuggestion> Suggestions,
    IReadOnlyList<WorkbenchChoiceGroup> Groups,
    WorkbenchChoice Fallback);

public sealed record WorkbenchSuggestionFacts(
    bool DocumentMismatch,
    bool AwaitingVendorSerial,
    bool DuplicateVendorSerial,
    bool OpenAssemblyWithUncapturedChildren);

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
    public const string Unclassified = "unclassified";
    public const string NoCommandFits = "no_command_fits";

    public static readonly IReadOnlyList<string> All = [Draft, AwaitingApproval, Approved, Rejected, Committed, Superseded, Unclassified, NoCommandFits];
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
    JsonElement? Result = null,
    string? UnclassifiedDescription = null,
    string? ClassifiedBy = null,
    DateTime? ClassifiedAt = null,
    string? EscalatedBy = null,
    DateTime? EscalatedAt = null);

public sealed record WorkbenchRequestPage(IReadOnlyList<WorkbenchRequestRow> Items, int Total, int Skip, int Take);

public sealed record RejectWorkbenchRequest(string Note);

public sealed record ApproveWorkbenchRequest(string? Note = null);

public sealed record FileUnclassifiedWorkbenchRequest(
    WorkbenchTarget Target,
    string Description,
    IReadOnlyList<WorkbenchEvidenceRef>? Evidence = null);

public sealed record RefileWorkbenchRequest(WorkbenchPreviewRequest Request);

public sealed record EscalateWorkbenchRequest(string Note);

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
    Guid? ParentEntitlementId,
    string? CoverageFamilyName);

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
