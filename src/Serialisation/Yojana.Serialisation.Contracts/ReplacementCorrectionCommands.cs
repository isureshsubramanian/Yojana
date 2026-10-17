namespace Yojana.Serialisation.Contracts;

/// <summary>
/// Corrects a shipped customer-facing replacement whose recorded replacement unit is not the physical unit supplied.
/// The link and actual unit are user inputs; policy, replacement reason, owner, site and effective shipment time are
/// recovered from the recorded replacement and cannot be overridden by a client.
/// </summary>
public sealed record CorrectReplacementLink(
    Guid CommandId,
    Guid RecordedReplacementUnitId,
    Guid ActualReplacementUnitId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

public sealed record PreviewCorrectReplacementLink(
    Guid RecordedReplacementUnitId,
    Guid ActualReplacementUnitId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

public sealed record CorrectReplacementLinkResult(
    Guid CorrectionId,
    Guid PreviousReplacementLinkId,
    Guid CorrectedReplacementLinkId,
    Guid RecordedReplacementUnitId,
    Guid ActualReplacementUnitId,
    Guid VoidedEntitlementId,
    Guid RestoredSourceEntitlementId,
    Guid TargetEntitlementId,
    string ReplacementReason,
    string PolicyApplied,
    DateOnly ComputedEnd,
    bool AssetTagRepointed,
    Guid EventId,
    bool Replayed);

