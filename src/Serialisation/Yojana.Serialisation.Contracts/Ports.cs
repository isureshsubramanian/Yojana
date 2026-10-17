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
    bool ExpiryRequired = false)
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
