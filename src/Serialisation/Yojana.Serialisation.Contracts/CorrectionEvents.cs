namespace Yojana.Serialisation.Contracts;

public sealed record PublishedCorrection(
    Guid CorrectionId,
    string ReasonCode,
    string InitiatedVia,
    Guid? CorrectsCorrectionId,
    IReadOnlyList<string> ApproverRefs);

/// <summary>Header for published facts that summarise several units, or do not belong to one unit.</summary>
public sealed record PublishedSummaryHeader(
    string Schema,
    string Version,
    Guid EventId,
    DateTime EventTime,
    DateTime RecordedTime,
    Guid LegalEntityId,
    string ActorRef,
    DocumentRef? Document);

/// <summary>
/// Summary events are published by the command handler through the transactional outbox, rather than by the per-unit
/// ledger mapper, because no single UnitEvent can represent the complete multi-unit operation.
/// </summary>
public interface IPublishedSummaryEvent
{
    PublishedSummaryHeader Header { get; }
}

public sealed record UnitIdentifiersSwapped(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    Guid SwapId,
    Guid CounterpartUnitId,
    PublishedSwappedIdentifier GivenIdentifier,
    PublishedSwappedIdentifier ReceivedIdentifier) : IPublishedUnitEvent
{
    public const string Schema = "Unit.IdentifiersSwapped";
    public const string Version = "1.0";
}

public sealed record PublishedSwappedIdentifier(Guid IdentifierId, string Type, string Scheme, string Value, Guid? IssuerPartyRef);

/// <summary>
/// Transactional-outbox request for P6's label service. P3 records the request only; it does not select a template,
/// render a label or report a print outcome.
/// </summary>
public sealed record AssetTagPrintJobRequested(
    Guid RequestId,
    Guid UnitId,
    Guid IdentifierId,
    string AssetTag,
    Guid SwapId,
    Guid CorrectionId,
    Guid TriggeringEventId,
    DateTime RequestedAt,
    Guid LegalEntityId,
    string ActorRef);

/// <summary>
/// Transactional-outbox request for labels whose entitlement-derived values changed after a source-document correction.
/// P3 records the request only; P6 selects the template and renders the replacement label.
/// </summary>
public sealed record LabelReprintJobRequested(
    Guid RequestId,
    Guid UnitId,
    Guid CorrectionId,
    Guid TriggeringEventId,
    DocumentRef CorrectedSourceDocument,
    string Reason,
    DateTime RequestedAt,
    Guid LegalEntityId,
    string ActorRef);

public sealed record UnitItemCorrected(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    Guid PreviousItemId,
    Guid CorrectedItemId,
    Guid PreviousAssignmentId,
    Guid NewAssignmentId,
    bool DocumentMismatch,
    string? InventoryReclassificationRef,
    bool WarrantyPolicyChanged) : IPublishedUnitEvent
{
    public const string Schema = "Unit.ItemCorrected";
    public const string Version = "1.0";
}

public sealed record UnitAsBuiltCorrected(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    string Change,
    DateTime EffectiveAt,
    PublishedAssemblyLink? Removed,
    PublishedAssemblyLink? Installed,
    IReadOnlyList<PublishedAsBuiltChange>? Changes = null) : IPublishedUnitEvent
{
    public const string Schema = "Unit.AsBuiltCorrected";
    public const string Version = "1.0";
}

public sealed record PublishedAsBuiltChange(string Change, PublishedAssemblyLink? Removed, PublishedAssemblyLink? Installed);

public sealed record PublishedAssemblyLink(
    Guid AssemblyLinkId,
    Guid? ChildUnitId,
    Guid? ChildItemId,
    Guid? ChildLotId,
    decimal? Quantity,
    string Relation,
    string? Slot);

public sealed record PublishedLateStockCoverage(
    Guid? LocationId,
    string LocationKind,
    int OpenQuantity,
    int CommissionedQuantity,
    int ExemptQuantity,
    string? ExemptReason,
    DocumentRef SourceDocument);

/// <summary>Serialisation's effective-dated item tracking fact, published without exposing its owned table.</summary>
public sealed record ItemTrackingModeChanged(
    PublishedSummaryHeader Header,
    PublishedCorrection Correction,
    Guid ItemId,
    string PreviousTrackingMode,
    string TrackingMode,
    DateTime EffectiveAt,
    bool Reversed,
    IReadOnlyList<PublishedLateStockCoverage> StockCoverage) : IPublishedSummaryEvent
{
    public const string FactType = ItemEventType.TrackingModeChanged;
    public const string Schema = "Item.TrackingModeChanged";
    public const string Version = "1.0";
}

public sealed record UnitVoided(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    IReadOnlyList<Guid> ClosedIdentifierIds,
    string SerialReuseOutcome,
    DocumentRef? ProductionOrder) : IPublishedUnitEvent
{
    public const string Schema = "Unit.Voided";
    public const string Version = "1.0";
}

public sealed record UnitVoidReversed(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    IReadOnlyList<Guid> RestoredIdentifierIds,
    string RestoredLifecycleStatus,
    DocumentRef? ProductionOrder) : IPublishedUnitEvent
{
    public const string Schema = "Unit.VoidReversed";
    public const string Version = "1.0";
}

public sealed record UnitMerged(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    Guid SurvivorUnitId,
    Guid AbsorbedUnitId,
    string Role) : IPublishedUnitEvent
{
    public const string Schema = "Unit.Merged";
    public const string Version = "1.0";
}

public sealed record UnitIdentifiersAssigned(
    PublishedSummaryHeader Header,
    PublishedCorrection Correction,
    DocumentRef ProductionOrder,
    IReadOnlyList<PublishedIdentifierAssignment> Assignments,
    int RemainingCount,
    IReadOnlyList<Guid> AwaitingVendorSerialUnitIds) : IPublishedSummaryEvent
{
    public const string Schema = "Unit.IdentifiersAssigned";
    public const string Version = "1.0";
}

public sealed record PublishedIdentifierAssignment(Guid UnitId, Guid IdentifierId, PublishedIdentifier Identifier);

public sealed record UnitAllocationReassigned(
    PublishedSummaryHeader Header,
    PublishedCorrection Correction,
    IReadOnlyList<PublishedAllocationReassignment> Reassignments) : IPublishedSummaryEvent
{
    public const string Schema = "Unit.AllocationReassigned";
    public const string Version = "1.1";
}

public sealed record PublishedAllocationReassignment(string Serial, Guid SerialRangeId, DocumentRef PreviousProductionOrder, DocumentRef NewProductionOrder);

public sealed record DocumentSerialsCorrected(
    PublishedSummaryHeader Header,
    PublishedCorrection Correction,
    DocumentRef Document,
    IReadOnlyList<Guid> ReversedUnitIds,
    IReadOnlyList<Guid> ShippedUnitIds,
    bool WholeListReplacement,
    bool FinanceReviewRequired) : IPublishedSummaryEvent
{
    public const string Schema = "Document.SerialsCorrected";
    public const string Version = "1.0";
}

public sealed record OwnershipSourceDocumentCorrected(
    PublishedSummaryHeader Header,
    PublishedCorrection Correction,
    IReadOnlyList<Guid> UnitIds,
    DocumentRef PreviousOpeningDocument,
    DocumentRef CorrectedOpeningDocument,
    bool OwnerChanged,
    Guid? PreviousOwnerPartyRef,
    Guid? CorrectedOwnerPartyRef) : IPublishedSummaryEvent
{
    public const string Schema = "Ownership.SourceDocumentCorrected";
    public const string Version = "1.0";
}
