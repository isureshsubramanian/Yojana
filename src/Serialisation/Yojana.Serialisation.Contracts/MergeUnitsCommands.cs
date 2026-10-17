namespace Yojana.Serialisation.Contracts;

public static class MergeUnitsResolution
{
    public const string Merge = "merge";
    public const string AcknowledgeVendorSerialDuplicate = "duplicate_acknowledged";
    public static readonly IReadOnlyList<string> All = [Merge, AcknowledgeVendorSerialDuplicate];
}

/// <summary>Preview two records before deciding whether they are one unit or a genuine vendor-serial collision.</summary>
public sealed record PreviewMergeUnits(
    Guid FirstUnitId,
    Guid SecondUnitId,
    DateTime EventTime,
    Guid? SurvivorUnitId = null,
    CorrectionCommitter? CommittedBy = null,
    IReadOnlyList<CorrectionApprover>? Approvers = null);

public sealed record MergeUnitsAlternative(string Resolution, bool Available, string Summary, CommandRefusal? Refusal = null);

public sealed record MergeUnitsPreviewResult(
    Guid RecommendedSurvivorUnitId,
    Guid SurvivorUnitId,
    Guid AbsorbedUnitId,
    CorrectionPreview Preview,
    CorrectionApprovalDecision Approval,
    IReadOnlyList<MergeUnitsAlternative> Alternatives);

/// <summary>
/// Resolves two unit records. <c>merge</c> leaves the absorbed record as a readable alias and re-points its rows;
/// <c>duplicate_acknowledged</c> keeps both units and records the approver's vendor-serial collision decision.
/// A later merge correction reverses an earlier one when <see cref="CorrectionMetadata.CorrectsCorrectionId"/> names it.
/// </summary>
public sealed record MergeUnits(
    Guid CommandId,
    Guid SurvivorUnitId,
    Guid AbsorbedUnitId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string Resolution = MergeUnitsResolution.Merge,
    string? VendorSerialValue = null,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

public sealed record MergeUnitsResult(
    Guid SurvivorUnitId,
    Guid AbsorbedUnitId,
    string Resolution,
    Guid CorrectionId,
    int RowsRepointed,
    bool Reversed,
    bool Replayed);
