namespace Yojana.Serialisation.Contracts;

/// <summary>What the Serialisation module needs to know about an item (ADR-0001 MOD-03 ports). Implemented by connectors or in-house modules; the module never reads an item table.</summary>
public sealed record ItemSerialisationProfile(
    Guid ItemId,
    string ItemCode,
    string TrackingMode,
    string SerialisationPoint,
    string RetentionClass,
    string SerialReusePolicy,
    string AsBuiltCompleteness,
    string SerialScheme,
    bool ExpiryRequired = false,
    /// <summary>Stable item-class key used by tenant correction policy overrides; null means the tenant default.</summary>
    string? ItemClass = null,
    bool SalesBlocked = false,
    bool ServiceBlocked = false,
    Guid? SuccessorItemId = null,
    DateOnly? LastServiceDate = null,
    /// <summary>Item-class policy fact: a unit cannot be sold or shipped until its media sanitisation is recorded.</summary>
    bool RequiresSanitisation = false,
    /// <summary>Model and part numbers printed on the item label; used to keep them out of serial fields.</summary>
    IReadOnlyList<string>? ModelNumbers = null)
{
    public static class TrackingModes
    {
        public const string Serial = "serial";
        public const string Lot = "lot";
        public const string None = "none";
    }

    public static class ReusePolicies
    {
        /// <summary>Default (OD-07): a serial is never re-issued, even after scrap.</summary>
        public const string Never = "never";

        /// <summary>Scrap-and-reuse creates a new unit with <c>derived_from_unit_id</c> (case 13).</summary>
        public const string AfterScrap = "after_scrap";
    }

    public static class Completeness
    {
        public const string Block = "block";
        public const string Warn = "warn";
    }
}

/// <summary>Item facts the module needs; <c>null</c> when the item is unknown to the host.</summary>
public interface IItemQueryPort
{
    Task<ItemSerialisationProfile?> GetAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Reads item facts for a selection in one port call; implementations must not turn this into one remote call per item.</summary>
    Task<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>> GetManyAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}

/// <summary>One on-hand quantity bucket as seen by Inventory; Serialisation never reads an Inventory table.</summary>
public sealed record OpenStockQuantity(Guid? LocationId, string LocationKind, decimal Quantity);

/// <summary>
/// Narrow inventory query used to prove a late tracking-mode change covers every open stock unit. The in-house or
/// connector implementation remains responsible for what counts as open stock at the effective time.
/// </summary>
public interface IStockQuantityPort
{
    Task<IReadOnlyList<OpenStockQuantity>> GetOpenStockAsync(Guid itemId, DateTime effectiveAt, CancellationToken cancellationToken);
}

/// <summary>Serialised units currently represented in one ERP on-hand bucket.</summary>
public sealed record SerialisedOnHandCount(Guid ItemId, Guid LocationId, long UnitCount);

/// <summary>The single custody policy used when comparing serialised units with ERP warehouse on-hand.</summary>
public static class SerialisedOnHandCustodyPolicy
{
    // Included regardless of disposition: warehouse/bin stock plus units physically held in quarantine (including
    // awaiting inspection), internal repair, or a stock-holding 3PL. Excluded custody is customer_site (including
    // loaner out), vendor/at_subcontractor (supplier custody), in_transit/returning, scrapped, or no location. Terminal
    // lifecycle rows (void, reversed, scrapped, decommissioned and merged aliases) and children consumed into an open assembly
    // are excluded separately by the query. Keep both lists explicit so a new custody kind cannot silently become stock.
    public static readonly IReadOnlyList<string> IncludedLocationKinds =
        [LocationKind.Warehouse, LocationKind.Bin, LocationKind.Quarantine, LocationKind.InRepair, LocationKind.At3pl];

    public static readonly IReadOnlyList<string> ExcludedLocationKinds =
        [LocationKind.CustomerSite, LocationKind.Vendor, LocationKind.AtSubcontractor, LocationKind.InTransit,
            LocationKind.Returning, LocationKind.InProduction, LocationKind.Scrapped];

    public static readonly IReadOnlyList<string> ExcludedLifecycleStatuses =
        [LifecycleStatus.Void, LifecycleStatus.Reversed, LifecycleStatus.ScrappedPreRelease, LifecycleStatus.Scrapped,
            LifecycleStatus.Decommissioned, LifecycleStatus.Merged];
}

/// <summary>
/// Narrow companion-mode query used by Integration's case-34 reconciliation. It exposes grouped counts only;
/// Integration never references Serialisation persistence or its current-state projection.
/// </summary>
public interface ISerialisedOnHandQuery
{
    Task<IReadOnlyList<SerialisedOnHandCount>> CountsAsync(CancellationToken cancellationToken);
}

/// <summary>One serialised component the BOM expects inside a parent (Identity rule 12).</summary>
public sealed record ExpectedComponent(string Slot, Guid ComponentItemId, string ComponentItemCode, int Quantity, Guid? ThroughPhantomItemId = null);

public sealed record ExpectedComponentQuery(Guid ItemId, DateOnly EffectiveAt);

/// <summary>One source BOM line. Unknown item identities remain visible through their source item code.</summary>
public sealed record BillOfMaterialsLine(
    int LineNo,
    string Kind,
    Guid? ItemId,
    string? ItemCode,
    string Description,
    decimal Quantity,
    string? Uom,
    bool Serialised,
    bool? InventoryItem);

/// <summary>The latest one-level BOM snapshot received from the source system.</summary>
public sealed record BillOfMaterials(DateTimeOffset AsAt, IReadOnlyList<BillOfMaterialsLine> Lines);

/// <summary>Expected serialised components of an item at a date (phantoms expanded).</summary>
public interface IBomQueryPort
{
    Task<BillOfMaterials?> BillOfMaterialsAsync(Guid itemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpectedComponent>> ExpectedSerialisedComponentsAsync(Guid itemId, DateOnly effectiveAt, CancellationToken cancellationToken);

    /// <summary>Reads all distinct item/date BOMs for a selection in one port call.</summary>
    Task<IReadOnlyDictionary<ExpectedComponentQuery, IReadOnlyList<ExpectedComponent>>> ExpectedSerialisedComponentsAsync(
        IReadOnlyCollection<ExpectedComponentQuery> queries, CancellationToken cancellationToken);
}

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  CorrectItem calls IStockReclassificationPort.ReclassifyAsync(...) inside the SAME database transaction that
//        closes the old UnitItemAssignment, opens the corrected one and writes unit.item_corrected.
// WHY:   The invariant "on-hand quantity of a serial-tracked item at a location equals the count of units in stock at
//        that location" must hold for both the previous and corrected items at every commit. An outbox or event hand-off
//        could commit the serial under one item while Inventory still holds the quantity under the other.
// WHAT BREAKS IF SEPARATED: stock quantities and serial counts diverge on a crash or retry between the two writes, and
//        reconciliation reports a mismatch with no atomic posting to reverse. Change the port signature and this comment
//        together with the CorrectItem caller and the future Inventory implementation.
// Engine: SQL Server / Azure SQL; the in-house pairing relies on a single local transaction, never on a distributed one.

/// <summary>One unit moving between item buckets without changing total stock quantity.</summary>
public sealed record StockReclassificationRequest(
    Guid CommandId,
    Guid CorrectionId,
    Guid UnitId,
    Guid PreviousItemId,
    Guid CorrectedItemId,
    Guid? LocationId,
    string? LocationKind,
    string Disposition,
    DateTime EventTime,
    string ActorRef,
    string ReasonCode);

public sealed record StockReclassificationResult(string PostingReference);

/// <summary>
/// Coupling-1 seam for an in-stock item correction. The P12 in-house implementation must enlist its stock posting in
/// the caller's local transaction; the P8 connector implementation supplies the companion-mode equivalent.
/// </summary>
public interface IStockReclassificationPort
{
    Task<StockReclassificationResult> ReclassifyAsync(StockReclassificationRequest request, CancellationToken cancellationToken);
}

/// <summary>The serial-side correction paired with an already posted stock document; quantity delta must be zero.</summary>
public sealed record StockPostingSerialCorrection(
    DocumentRef Document,
    Guid ItemId,
    decimal QuantityDelta,
    IReadOnlyList<Guid> RecordedUnitIds,
    IReadOnlyList<Guid> PhysicalUnitIds,
    Guid CorrectionId);

/// <summary>The stock issue paired with a shipped replacement; Inventory owns valuation and the posting reference.</summary>
public sealed record WarrantyReplacementStockIssue(
    Guid CommandId,
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    Guid ReplacementLinkId,
    Guid UnitId,
    Guid ItemId,
    DateTime EventTime,
    string ActorRef,
    Guid LegalEntityId);

public sealed record WarrantyReplacementStockIssueResult(string PostingReference);

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  Warranty replacement calls IStockUnitEventPort.IssueWarrantyReplacementAsync(...), while document and
//        replacement-link corrections call IStockUnitEventPort.RecordCorrectionForPosting(...); Inventory must handle each inside the SAME database
//        transaction that writes the corresponding stock ledger line and valuation layer.
// WHY:   The invariant "on-hand quantity of a serial-tracked item at a location equals the count of
//        units in stock at that location" (ADR-0001, edge case 34) must hold at every commit. An outbox
//        or event hand-off would allow a committed quantity with no units (or units with no quantity)
//        between the two writes, which the nightly check would only detect, not prevent.
// WHAT BREAKS IF SEPARATED: serial counts and stock quantities can diverge on any crash or retry
//        between the two writes; the WarrantyReplacement and return receipts could post stock without
//        serial events; the reconciliation job would emit mismatch_quantity exceptions that no one can
//        resolve from history. Change the port signature and this comment together, on both sides.
// NOTE:  Serialisation.CorrectDocumentSerials (ADR-0001 cases 32 and 141) and CorrectReplacementLink (case 144) call RecordCorrectionForPosting(...)
//        on this same port with a quantity delta of ZERO: it replaces which units a posted document
//        refers to, or removes units recorded as shipped when none physically left, while the posted document's
//        quantity remains unchanged. The port asserts the zero delta; a quantity correction is a document reversal.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a distributed one.
public interface IStockUnitEventPort
{
    Task<WarrantyReplacementStockIssueResult> IssueWarrantyReplacementAsync(WarrantyReplacementStockIssue issue, CancellationToken cancellationToken);

    Task RecordCorrectionForPosting(StockPostingSerialCorrection correction, CancellationToken cancellationToken);

    // P5 RMA return-receipt extension — keep this block separate so sibling port additions merge mechanically.
    /// <summary>Pairs an RMA return receipt with Inventory's quantity/value posting in the same local transaction.</summary>
    Task RecordReturnForPosting(StockReturnPosting request, CancellationToken cancellationToken);
    // End P5 RMA return-receipt extension.
}

public sealed record StockReturnPosting(
    Guid CommandId,
    Guid ReturnReceiptId,
    Guid UnitId,
    Guid RmaId,
    Guid RmaLineId,
    Guid ReceivingLocationId,
    string ConditionGrade,
    string ValuationRule,
    DateTime ReceivedAt,
    string PostingPeriod);

/// <summary>Facts resolved from the source aggregate; RMA supplies its implementation when that sibling module is merged.</summary>
public sealed record ReplacementSource(
    string Kind,
    Guid Id,
    string Number,
    Guid OriginalUnitId,
    Guid SourceEntitlementId,
    DateOnly CoverageDate,
    DocumentRef? OriginalSale,
    string? ReturnReasonCode);

public interface IReplacementSourceQueryPort
{
    Task<ReplacementSource?> ResolveAsync(string kind, Guid id, Guid sourceEntitlementId, CancellationToken cancellationToken);
}

/// <summary>Return owns rule interpretation; Replacement receives only the auditable rule and computed service amount.</summary>
public sealed record RestockingFeeQuote(string Rule, decimal Amount);

public interface IReturnReasonQueryPort
{
    Task<RestockingFeeQuote> QuoteAsync(string? returnReasonCode, decimal basis, CancellationToken cancellationToken);
}

// P5 refurbishment hand-off — keep this block separate so sibling port additions merge mechanically.
/// <summary>RMA asks refurbishment to open work without knowing its tables or handler implementation.</summary>
public interface IRefurbishmentOpener
{
    Task<RefurbishmentOpenReceipt> OpenAsync(RefurbishmentOpenRequest request, CancellationToken cancellationToken);
}

public sealed record RefurbishmentOpenRequest(
    Guid CommandId,
    Guid TargetUnitId,
    Guid SourceRmaLineId,
    DocumentRef SourceDocument,
    DateTime OpenedAt,
    string ActorRef,
    Guid LegalEntityId,
    Guid? RepairLocationId = null);

public sealed record RefurbishmentOpenReceipt(Guid WorkOrderId, string WorkOrderNumber, bool Replayed);
// End P5 refurbishment hand-off.

// P5 RMA follow-up hand-off — RMA closure depends only on these completion facts, not sibling tables.
/// <summary>Reports the incomplete external work that prevents an RMA from closing.</summary>
public interface IRmaFollowUpQueryPort
{
    Task<IReadOnlyList<RmaOpenFollowUp>> OpenAsync(Guid rmaId, CancellationToken cancellationToken);
}
// End P5 RMA follow-up hand-off.
