namespace Yojana.Serialisation.Contracts;

// Commands of the Serialisation module (ADR-0001 "Integration boundaries and coupling": synchronous, in-process,
// validated). Other modules send them through the message bus and receive the result record; nothing here exposes
// a table. Every command carries the actor, the business time and a command id that makes it idempotent.

/// <summary>A document reference as every command and event carries it.</summary>
public sealed record DocumentRef(string Type, Guid Id, int? Line = null);

/// <summary>One identifier to put on a unit: the type decides scheme, normalisation and validation.</summary>
public sealed record IdentifierInput(string Type, string RawValue, Guid? IssuerPartyRef = null, string? Qualifier = null, string? EnterpriseIdentifier = null);

/// <summary>One unit to commission: its identifiers (none for a pending-identifier unit) and an optional caller key to match results back.</summary>
public sealed record CommissionLine(IReadOnlyList<IdentifierInput> Identifiers, string? ClientReference = null, Guid? LotId = null, DateOnly? ExpiryDate = null);

/// <summary>
/// Bring units into existence (ADR-0001 PhysicalUnit state machine). Manufactured units start <c>pre_release</c>,
/// purchased units <c>released</c>; a line without identifiers creates a <c>pending_identifier</c> unit when the
/// item's serialisation point allows it (case 6). The opening custody row is written from the location given.
/// </summary>
public sealed record CommissionUnits(
    Guid CommandId,
    Guid ItemId,
    string Origin,
    IReadOnlyList<CommissionLine> Lines,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    Guid? ModelVersionId = null,
    DocumentRef? Document = null,
    Guid? LocationId = null,
    string? LocationKind = null,
    string? Source = null,
    bool SkipInvalidLines = false,
    /// <summary>Case 22: a trade-in of a foreign brand gets a unit and a graded value but is not ours; null = true except for trade-ins.</summary>
    bool? BrandIsOurs = null,
    /// <summary>How much earlier history is known; claim reconstruction and migration use opening-only instead of inventing facts.</summary>
    string HistoryDepth = Yojana.Serialisation.Contracts.HistoryDepth.Full,
    bool ConfirmedNotModelNumber = false,
    string? ModelNumberConfirmationReason = null);

public sealed record CommissionLineResult(int Index, string? ClientReference, Guid? UnitId, string Outcome, IReadOnlyList<string> Reasons)
{
    public const string Commissioned = "commissioned";
    public const string Pending = "pending_identifier";
    public const string Duplicate = "duplicate";
    public const string Refused = "refused";
}

public sealed record CommissionUnitsResult(IReadOnlyList<CommissionLineResult> Lines, int Commissioned, int Pending, int Refused, int Duplicates);

/// <summary>Put an identifier on an existing unit: the first one on a pending unit moves it on (case 6); an extra type adds a row.</summary>
public sealed record AssignIdentifier(Guid CommandId, Guid UnitId, IdentifierInput Identifier, DateTime EventTime, string ActorRef,
    Guid LegalEntityId, DocumentRef? Document = null, bool ConfirmedNotModelNumber = false,
    string? ModelNumberConfirmationReason = null);

public sealed record AssignIdentifierResult(Guid UnitId, Guid IdentifierId, string NormalisedValue, string LifecycleStatus);

/// <summary>One approval attached to a correction; roles are recorded as evidence, never inferred later.</summary>
public sealed record CorrectionApprover(string UserRef, IReadOnlyList<string> Roles);

/// <summary>Who asks to commit a correction and the tenant roles established by its trusted entry point.</summary>
public sealed record CorrectionCommitter(string UserRef, IReadOnlyList<string> Roles);

/// <summary>Metadata shared by every correction command and every ledger fact it writes.</summary>
public sealed record CorrectionMetadata(
    Guid CorrectionId,
    string ReasonCode,
    string? Note = null,
    IReadOnlyList<string>? EvidenceRefs = null,
    string InitiatedVia = CorrectionInitiatedVia.Api,
    Guid? CorrectsCorrectionId = null,
    string? InitiatingRole = null,
    IReadOnlyList<CorrectionApprover>? Approvers = null);

/// <summary>
/// Identity rule 5: a new identifier row replaces the open one of that type; the old row closes with the reason and
/// points at its successor; the unit's key and every event stay as they are; no stock moves.
/// </summary>
public sealed record CorrectIdentifier(Guid CommandId, Guid UnitId, string IdentifierType, string NewRawValue, string Reason,
    DateTime EventTime, string ActorRef, Guid LegalEntityId, string? ApproverRef = null, string? Note = null,
    Guid? IssuerPartyRef = null, string? Qualifier = null, CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null, bool ConfirmedNotModelNumber = false,
    string? ModelNumberConfirmationReason = null);

/// <summary>Rehearses <see cref="CorrectIdentifier"/> through the real handler in a rolled-back transaction.</summary>
public sealed record PreviewCorrectIdentifier(CorrectIdentifier Command);

public sealed record CorrectIdentifierResult(Guid UnitId, Guid SupersededIdentifierId, Guid NewIdentifierId, string OldNormalisedValue, string NewNormalisedValue, Guid CorrectionId = default);

/// <summary>
/// Correct two units whose identifiers were cross-recorded. Both current rows close and both values reopen on the
/// opposite unit in one transaction; <see cref="CorrectionMetadata.CorrectsCorrectionId"/> names an earlier swap when
/// this swap reverses it.
/// </summary>
public sealed record SwapIdentifiers(
    Guid CommandId,
    Guid FirstUnitId,
    Guid SecondUnitId,
    string IdentifierType,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string? Note = null,
    DocumentRef? Document = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null,
    Guid? SwapId = null);

/// <summary>The dry-run path used by the workbench and the approver view; it writes nothing.</summary>
public sealed record PreviewSwapIdentifiers(SwapIdentifiers Command);

public sealed record SwapIdentifiersResult(
    Guid SwapId,
    Guid FirstUnitId,
    Guid SecondUnitId,
    Guid FirstNewIdentifierId,
    Guid SecondNewIdentifierId,
    Guid CorrectionId,
    int AssetTagPrintJobsRequested,
    bool Replayed);

/// <summary>
/// Void an allocated or kitted unit that was never shipped or named on an external document. A command whose
/// correction metadata references an earlier VoidUnit correction reverses that void instead.
/// </summary>
public sealed record VoidUnit(
    Guid CommandId,
    Guid UnitId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    DocumentRef? ProductionOrder = null,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

/// <summary>Rehearse <see cref="VoidUnit"/> without recording facts or changing state.</summary>
public sealed record PreviewVoidUnit(VoidUnit Command);

public sealed record VoidUnitResult(
    Guid UnitId,
    string PreviousLifecycleStatus,
    string LifecycleStatus,
    IReadOnlyList<Guid> IdentifierIds,
    string SerialReuseOutcome,
    Guid CorrectionId,
    bool Reversed,
    bool Replayed = false);

/// <summary>
/// Replaces the serial side of a posted delivery, transfer or receipt without changing stock quantity (ADR-0001 case
/// 141). The two stored selections are paired by item and normalised serial order; they must have equal counts per item.
/// </summary>
public sealed record CorrectDocumentSerials(
    Guid CommandId,
    DocumentRef Document,
    Guid RecordedSelectionId,
    Guid PhysicalSelectionId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

/// <summary>The dry-run form consumed by the workbench and approver view; it performs the command's full validation.</summary>
public sealed record PreviewCorrectDocumentSerials(
    DocumentRef Document,
    Guid RecordedSelectionId,
    Guid PhysicalSelectionId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

public sealed record CorrectDocumentSerialsResult(
    Guid CorrectionId,
    DocumentRef Document,
    int Reversed,
    int Shipped,
    int EntitlementsVoided,
    int EntitlementsCreated,
    bool WholeListReplacement,
    bool FinanceReviewRequired,
    bool Replayed = false);

/// <summary>
/// Corrects the ownership source for a stored selection. The ledger rows written by the previous document remain
/// immutable; compensating facts make <see cref="CorrectedDocument"/> the effective source. A reversal swaps the two
/// documents and names the earlier correction through <see cref="CorrectionMetadata.CorrectsCorrectionId"/>.
/// </summary>
public sealed record CorrectSourceDocument(
    Guid CommandId,
    Guid SelectionId,
    DocumentRef PreviousDocument,
    DocumentRef CorrectedDocument,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

/// <summary>The dry-run form consumed by the workbench and approver view.</summary>
public sealed record PreviewCorrectSourceDocument(
    Guid SelectionId,
    DocumentRef PreviousDocument,
    DocumentRef CorrectedDocument,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null);

public sealed record CorrectSourceDocumentResult(
    Guid CorrectionId,
    int UnitsCorrected,
    int RelatedEventsRepointed,
    int EntitlementsSuperseded,
    int EntitlementsCreated,
    int LabelReprintsRequested,
    bool OwnerChanged,
    bool Replayed = false);

/// <summary>
/// Identity rule 13: selected reservations may move before commissioning without changing an identity. A pairwise
/// exchange pairs the two selections in their supplied order; a move uses only <see cref="FromSerials"/>. The serial
/// selections use the pasted-list and scheme-validated range shapes already used by bulk selection sets.
/// </summary>
public sealed record ReassignAllocation(
    Guid CommandId,
    DocumentRef FromProductionOrder,
    DocumentRef ToProductionOrder,
    string Mode,
    SelectionSource FromSerials,
    SelectionSource? ToSerials,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    string? Note = null,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null,
    bool PreviewOnly = false);

public sealed record ReassignAllocationResult(Guid CorrectionId, Guid? EventId, CorrectionPreview Preview, bool Replayed);

/// <summary>Release a <c>pre_release</c> unit on its release signature; freezes the as-built into BuildRecord version 1 (or the next version after reassembly).</summary>
public sealed record ReleaseUnit(Guid CommandId, Guid UnitId, DateTime EventTime, string ActorRef, Guid LegalEntityId, string ReleaseSignatureRef, DocumentRef? Document = null, string? Revision = null, Guid? FacilityId = null, string? TestResultsJson = null);

public sealed record ReleaseUnitResult(Guid UnitId, Guid BuildRecordId, int BuildRecordVersion, string LifecycleStatus);

/// <summary>
/// A command that a rule stops. The result names the rule and the step that unblocks it, so a screen or a
/// connector never has to guess (MOD-11: refusal results naming the rule and the unblocking step).
/// </summary>
public sealed record CommandRefusal(string Rule, string Message, string? UnblockingStep = null);

public class CommandRefusedException(string rule, string message, string? unblockingStep = null)
    : Yojana.Platform.Contracts.CommandRefusedException(rule, message, unblockingStep)
{
    public CommandRefusal Refusal => new(Rule, Message, UnblockingStep);
}

/// <summary>
/// Reserve serials for an order (ADR-0001 SerialRange, case 7, Identity rule 13): a contiguous block from
/// <paramref name="Start"/> (or the next free serial of the block when null), or a randomised list. Count always equals
/// <paramref name="Quantity"/>; a block that overlaps a reserved, commissioning, exhausted or released range is refused
/// with the next free start. A reservation is not an identity: no unit exists until commissioning.
/// </summary>
public sealed record AllocateSerialRange(
    Guid CommandId,
    string Scheme,
    int Quantity,
    DateTime EventTime,
    string ActorRef,
    string? Start = null,
    bool Randomised = false,
    Guid? ItemId = null,
    string? Qualifier = null,
    string? EnterpriseIdentifier = null,
    DocumentRef? AllocatedTo = null,
    Guid? AllocatedToPartyRef = null,
    string? Note = null,
    bool ConfirmedNotModelNumber = false,
    string? ModelNumberConfirmationReason = null,
    Guid LegalEntityId = default);

public sealed record AllocateSerialRangeResult(Guid RangeId, string Scheme, string? StartValue, string? EndValue, int Count, bool Randomised, IReadOnlyList<string> Serials, bool StartMoved, string? RequestedStart);

/// <summary>Details of an overlap refusal, carried on the exception's message and in this record for screens.</summary>
public sealed record RangeOverlap(Guid RangeId, string StartValue, string EndValue, string AllocatedTo, string NextFreeStart);

/// <summary>A child to install: a serialised unit, or an item (with lot) and quantity for lot and non-serialised children.</summary>
public sealed record ChildInput(Guid? ChildUnitId, Guid? ChildItemId = null, Guid? ChildLotId = null, decimal? Quantity = null, string Relation = "component", string? Slot = null, Guid? ThroughPhantomItemId = null, string? ReleaseStatus = null, string? ReleaseDocRef = null);

/// <summary>
/// Install children into a parent (Identity rules 8, 9; cases 10, 14, 35–38). <paramref name="Removals"/> closes links first
/// in the same unit of work, so a rework swap is one command. A dormant parent becomes released again (<c>unit.reassembled</c>).
/// </summary>
public sealed record RecordAssembly(Guid CommandId, Guid ParentUnitId, IReadOnlyList<ChildInput> Installs, DateTime EventTime, string ActorRef, Guid LegalEntityId, IReadOnlyList<Guid>? Removals = null, DocumentRef? Document = null, string? RemovalReason = null);

public sealed record RecordAssemblyResult(Guid ParentUnitId, IReadOnlyList<Guid> LinkIds, IReadOnlyList<Guid> ClosedLinkIds, string ParentLifecycleStatus);

/// <summary>Close links of a parent: the ones named, or every open link when <paramref name="LinkIds"/> is null; a parent left with no links becomes dormant.</summary>
public sealed record RecordDisassembly(Guid CommandId, Guid ParentUnitId, IReadOnlyList<Guid>? LinkIds, DateTime EventTime, string ActorRef, Guid LegalEntityId, string? Reason = null, DocumentRef? Document = null);

public sealed record RecordDisassemblyResult(Guid ParentUnitId, IReadOnlyList<Guid> ClosedLinkIds, string ParentLifecycleStatus);

/// <summary>A unit named either by its stable id or by one serial from a pasted list. Exactly one is supplied.</summary>
public sealed record AsBuiltUnitReference(
    Guid? UnitId = null,
    string? Serial = null,
    string? IdentifierType = null,
    Guid? IssuerPartyRef = null);

/// <summary>A serialised component, or an item/lot/quantity component, to write into the corrected tree.</summary>
public sealed record CorrectedAsBuiltComponent(
    AsBuiltUnitReference? Unit = null,
    Guid? ItemId = null,
    Guid? LotId = null,
    decimal? Quantity = null,
    string Relation = AssemblyRelation.Component,
    string? Slot = null,
    Guid? ThroughPhantomItemId = null,
    string? ReleaseStatus = null,
    string? ReleaseDocRef = null,
    DateTime? EffectiveUntil = null);

/// <summary>One link correction. Remove and replace name the retained AssemblyLink row; add and replace provide the fitted component.</summary>
public sealed record AsBuiltLinkCorrection(string Change, Guid? RemovedLinkId = null, CorrectedAsBuiltComponent? Installed = null);

/// <summary>All component corrections for one parent at one physical effective time; the command writes one parent fact.</summary>
public sealed record AsBuiltParentCorrection(AsBuiltUnitReference Parent, DateTime EffectiveAt, IReadOnlyList<AsBuiltLinkCorrection> Changes);

/// <summary>
/// Correct an as-built tree after the fact. Parents and serialised components may be supplied as a pasted serial list;
/// every target is validated before the atomic batch writes anything.
/// </summary>
public sealed record CorrectAsBuilt(
    Guid CommandId,
    IReadOnlyList<AsBuiltParentCorrection> Targets,
    string Reason,
    string ActorRef,
    Guid LegalEntityId,
    CorrectionMetadata? Correction = null,
    CorrectionCommitter? CommittedBy = null,
    string? Note = null);

/// <summary>Dry run of <see cref="CorrectAsBuilt"/> through the same planner and tenant approval policy.</summary>
public sealed record PreviewCorrectAsBuilt(CorrectAsBuilt Command);

public sealed record CorrectedAsBuiltParentResult(
    Guid ParentUnitId,
    Guid EventId,
    IReadOnlyList<Guid> InstalledLinkIds,
    IReadOnlyList<Guid> RemovedLinkIds);

public sealed record CorrectAsBuiltResult(
    Guid CorrectionId,
    int ParentCount,
    int ChangeCount,
    IReadOnlyList<CorrectedAsBuiltParentResult> Parents,
    bool Replayed = false);

/// <summary>Existing open stock at one location, supplied as one serial per unit or covered by an explicit exemption.</summary>
public sealed record LateSerializationStockLocation(
    Guid CommissionCommandId,
    Guid? LocationId,
    string LocationKind,
    DocumentRef SourceDocument,
    IReadOnlyList<CommissionLine> Units,
    int ExemptQuantity = 0,
    string? ExemptReason = null,
    string Origin = UnitOrigin.Purchased);

/// <summary>
/// A non-serialised component line already fitted in a kit. LateSerialization commissions the supplied units, then
/// replaces this quantity link through the existing CorrectAsBuilt planner and writer.
/// </summary>
public sealed record LateSerializationKitCapture(
    Guid CommissionCommandId,
    Guid ExistingLinkId,
    DateTime FittedAt,
    DocumentRef SourceDocument,
    IReadOnlyList<CommissionLine> Units,
    string Origin = UnitOrigin.Purchased);

/// <summary>
/// Changes an item's effective tracking mode from non-serialised to serialised after stock exists. A later command
/// references this correction to reverse only the tracking change, and is refused once serialised activity exists.
/// </summary>
public sealed record LateSerialization(
    Guid CommandId,
    Guid ItemId,
    DateTime EffectiveAt,
    string Reason,
    string ActorRef,
    Guid LegalEntityId,
    DocumentRef SourceDocument,
    IReadOnlyList<LateSerializationStockLocation> Stock,
    IReadOnlyList<LateSerializationKitCapture> KitCaptures,
    CorrectionMetadata Correction,
    CorrectionCommitter CommittedBy,
    Guid? AsBuiltCommandId = null,
    string? Note = null);

/// <summary>Dry run of <see cref="LateSerialization"/> through the real command in a rolled-back transaction.</summary>
public sealed record PreviewLateSerialization(LateSerialization Command);

public sealed record LateSerializationResult(
    Guid TrackingModeChangeId,
    Guid CorrectionId,
    Guid ItemId,
    string PreviousTrackingMode,
    string TrackingMode,
    int Commissioned,
    int Exempted,
    int KitsCaptured,
    int KitsNeedingAsBuiltCapture,
    bool Reversed,
    bool Replayed = false);

/// <summary>
/// Change the item or model version a unit is (case 42): upgrade, conversion or relabel close the current
/// UnitItemAssignment and open the next one; the serial stays. Warranty rules decide later whether they evaluate the
/// item at sale or at claim; this command only records the fact.
/// </summary>
public sealed record AssignItem(Guid CommandId, Guid UnitId, Guid NewItemId, string Reason, DateTime EventTime, string ActorRef, Guid LegalEntityId, Guid? ModelVersionId = null, DocumentRef? Document = null, string? Note = null);

public sealed record AssignItemResult(Guid UnitId, Guid ClosedAssignmentId, Guid NewAssignmentId, Guid PreviousItemId, Guid NewItemId);

/// <summary>One warranty-family policy selection changed because the unit's corrected item differs.</summary>
public sealed record CorrectedItemWarrantyPolicy(string CoverageFamily, string? PreviousPolicyCode, string? CorrectedPolicyCode);

/// <summary>
/// Correct a unit recorded against the wrong item (ADR-0001 case 118). The current item assignment is closed and a
/// correction assignment is opened. Customer-owned units retain their Sales and Finance documents, gain a
/// <c>document_mismatch</c> flag and raise a finance-review obligation; all other units request stock reclassification.
/// </summary>
public sealed record CorrectItem(
    Guid CommandId,
    Guid UnitId,
    Guid CorrectedItemId,
    string Reason,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    CorrectionMetadata Correction,
    CorrectionCommitter CommittedBy,
    Guid? CorrectedModelVersionId = null);

/// <summary>Runs the same CorrectItem planning and approval checks without writing.</summary>
public sealed record PreviewCorrectItem(CorrectItem Command);

public sealed record CorrectItemResult(
    Guid UnitId,
    Guid PreviousAssignmentId,
    Guid NewAssignmentId,
    Guid PreviousItemId,
    Guid CorrectedItemId,
    bool Sold,
    bool DocumentMismatch,
    string? InventoryReclassificationRef,
    bool FinanceReviewRequired,
    IReadOnlyList<CorrectedItemWarrantyPolicy> WarrantyPolicyChanges,
    Guid CorrectionId,
    Guid EventId,
    bool Replayed);

/// <summary>Who the unit belongs to from now (ADR-0001 CustodyEpisode, ownership transitions). Closes the open ownership row in the same event when one exists (a transfer is one transaction).</summary>
public sealed record OpenOwnership(
    Guid CommandId, Guid UnitId, Guid PartyRef, string PartyType, string Reason, DateTime EventTime, string ActorRef, Guid LegalEntityId,
    string Source = "invoice", string Confidence = "high", DocumentRef? Document = null, string? CustomerPoRef = null, string? CustomerItemRef = null,
    bool PartyIsGroupMember = false, string? ExternalOrderRef = null, string? ClosingReason = null);

public sealed record OpenOwnershipResult(Guid UnitId, Guid EpisodeId, Guid? ClosedEpisodeId);

/// <summary>Ends the open ownership row without a successor (a return received before the unit is back in our books, a write-off settlement pending).</summary>
public sealed record CloseOwnership(Guid CommandId, Guid UnitId, string Reason, DateTime EventTime, string ActorRef, Guid LegalEntityId, DocumentRef? Document = null);

public sealed record CloseOwnershipResult(Guid UnitId, Guid ClosedEpisodeId);

/// <summary>Where the unit physically is from now (custody transitions table). One event closes the old row and opens the new.</summary>
public sealed record ChangeCustody(
    Guid CommandId, Guid UnitId, string LocationKind, string Reason, DateTime EventTime, string ActorRef, Guid LegalEntityId,
    Guid? LocationId = null, Guid? PartyRef = null, string? PartyType = null, string Source = "delivery", string Confidence = "high",
    DateOnly? DueBack = null, DocumentRef? Document = null, string? ExternalOrderRef = null,
    CorrectionCommitter? CommittedBy = null);

public sealed record ChangeCustodyResult(Guid UnitId, Guid EpisodeId, Guid? ClosedEpisodeId, string FromLocationKind, string ToLocationKind);

/// <summary>
/// Case 24: a 3PL confirms the serials of a shipment line that was posted without them. The <c>unit.shipped</c> events
/// take the physical ship time as business time; the document is not re-posted.
/// </summary>
public sealed record ConfirmShipmentSerials(Guid CommandId, DocumentRef Document, int Line, IReadOnlyList<DocumentSerial> Serials, DateTime PhysicalShipTime, string ActorRef, Guid LegalEntityId, string Source = "three_pl", string? SerialCaptureMethod = null);

public sealed record ConfirmShipmentSerialsResult(Guid PendingId, int Shipped, int Remaining, IReadOnlyList<string> Exceptions);

/// <summary>Case 30 (and 31, 67, 76): overlay flags (lost, stolen, written_off, clone_seen, recalled, authenticity_failed) sit on any status and block entitlement evaluation; setting and clearing are events, never edits.</summary>
public sealed record SetUnitFlag(Guid CommandId, Guid UnitId, string Flag, DateTime EventTime, string ActorRef, Guid LegalEntityId, string? Reason = null, DocumentRef? Document = null);

public sealed record ClearUnitFlag(Guid CommandId, Guid UnitId, string Flag, DateTime EventTime, string ActorRef, Guid LegalEntityId, string? Reason = null, DocumentRef? Document = null);

public sealed record UnitFlagResult(Guid UnitId, IReadOnlyList<string> Flags, bool Changed, bool InvestigationRaised);

/// <summary>
/// Records unit attributes such as device firmware. Values are merged into the current attribute map and retained in
/// effective-dated history; a null value removes the current key while preserving that removal as a history fact.
/// </summary>
public sealed record RecordUnitAttributes(
    Guid CommandId,
    Guid UnitId,
    IReadOnlyDictionary<string, string?> Attributes,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId,
    DocumentRef? Document = null);

public sealed record RecordUnitAttributesRequest(
    Guid CommandId,
    IReadOnlyDictionary<string, string?> Attributes,
    DateTime EventTime,
    Guid LegalEntityId,
    DocumentRef? Document = null);

public sealed record RecordUnitAttributesResult(Guid UnitId, IReadOnlyDictionary<string, string?> Attributes, Guid EventId, bool Replayed);
