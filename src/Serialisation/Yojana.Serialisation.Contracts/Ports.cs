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
    string? ItemClass = null)
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
}

/// <summary>One serialised component the BOM expects inside a parent (Identity rule 12).</summary>
public sealed record ExpectedComponent(string Slot, Guid ComponentItemId, string ComponentItemCode, int Quantity, Guid? ThroughPhantomItemId = null);

/// <summary>Expected serialised components of an item at a date (phantoms expanded).</summary>
public interface IBomQueryPort
{
    Task<IReadOnlyList<ExpectedComponent>> ExpectedSerialisedComponentsAsync(Guid itemId, DateOnly effectiveAt, CancellationToken cancellationToken);
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

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  Inventory.StockPostingService calls Serialisation.IStockUnitEventPort.RecordForPosting(...)
//        inside the SAME database transaction that writes the stock ledger line and valuation layer.
// WHY:   The invariant "on-hand quantity of a serial-tracked item at a location equals the count of
//        units in stock at that location" (ADR-0001, edge case 34) must hold at every commit. An outbox
//        or event hand-off would allow a committed quantity with no units (or units with no quantity)
//        between the two writes, which the nightly check would only detect, not prevent.
// WHAT BREAKS IF SEPARATED: serial counts and stock quantities can diverge on any crash or retry
//        between the two writes; the WarrantyReplacement and return receipts could post stock without
//        serial events; the reconciliation job would emit mismatch_quantity exceptions that no one can
//        resolve from history. Change the port signature and this comment together, on both sides.
// NOTE:  Serialisation.CorrectDocumentSerials (ADR-0001 case 141) calls RecordCorrectionForPosting(...)
//        on this same port with a quantity delta of ZERO: it replaces which units a posted document
//        refers to (same item, same quantity) and touches only the serial side of the pairing. The port
//        asserts the zero delta; a correction that would change quantity is a reversal plus a new posting.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a distributed one.
public interface IStockUnitEventPort
{
    Task RecordCorrectionForPosting(StockPostingSerialCorrection correction, CancellationToken cancellationToken);
}
