namespace Yojana.Serialisation.Contracts;

// Query ports the consumed-event handlers read documents through (MOD-05 contracts). Connectors (MOD-14) and in-house
// modules (MOD-22) implement them; the module never reads a document table. Shapes are the ADR-0002 line-level fields
// the module needs, with external ids already mapped to module references by the implementer.

/// <summary>A serial on a document line as the publisher knows it.</summary>
public sealed record DocumentSerial(string Value, Guid? IssuerPartyRef = null, string? IdentifierType = null, string? ErpSerialId = null);

public static class SerialCaptureMethodValue
{
    public const string Scanned = "scanned";
    public const string Copied = "copied";
    public const string Keyed = "keyed";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Scanned, Copied, Keyed], StringComparer.Ordinal);
}

public static class SerialConfirmationMode
{
    public const string ScanRequired = "scan_required";
    public const string CopyAllowed = "copy_allowed";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([ScanRequired, CopyAllowed], StringComparer.Ordinal);
}

public sealed record SerialConfirmationSetting(string Mode);

/// <summary>One line of any serial-bearing document.</summary>
public sealed record DocumentLine(
    int LineNo,
    Guid ItemId,
    decimal Quantity,
    IReadOnlyList<DocumentSerial> Serials,
    Guid? LocationId = null,
    string? LocationKind = null,
    Guid? LotId = null,
    DateOnly? ExpiryDate = null,
    DocumentRef? BaseDocument = null,
    Guid? PartyRef = null,
    string? ErpLineId = null,
    string? SerialCaptureMethod = null);

/// <summary>A goods receipt or, with <see cref="IsAsn"/>, a vendor's advance shipping notice; an ASN for a drop-ship names the vendor shipment confirmation it carries the serials for in <see cref="VendorShipment"/>.</summary>
public sealed record ReceiptDocument(DocumentRef Document, Guid VendorPartyRef, DateTime PostedAt, IReadOnlyList<DocumentLine> Lines, DocumentRef? BaseOrder = null, bool IsAsn = false, DocumentRef? VendorShipment = null, bool IsInvoiced = false);

/// <summary>Drop-ship confirmation: what the vendor shipped to which customer, with carrier evidence.</summary>
public sealed record VendorShipment(DocumentRef Document, Guid VendorPartyRef, Guid CustomerPartyRef, Guid? ShipToLocationId, DateTime ShippedAt, IReadOnlyList<DocumentLine> Lines, string? CarrierRef = null, DateTime? DeliveredAt = null);

/// <summary>A produced parent with the component lines consumed into it (serials where known).</summary>
public sealed record ProductionReceipt(DocumentRef Document, DocumentRef ProductionOrder, DateTime PostedAt, IReadOnlyList<DocumentLine> Produced, IReadOnlyList<ComponentIssue> Components, Guid? FacilityId = null);

/// <summary>The allocation facts Serialisation needs to decide whether reservations may move to a production order.</summary>
public sealed record ProductionOrderAllocation(DocumentRef ProductionOrder, Guid ItemId, string SerialScheme, int Quantity);

/// <summary>A component line issued into a parent serial (or into the order when the parent is not serialised yet).</summary>
public sealed record ComponentIssue(int LineNo, Guid ComponentItemId, decimal Quantity, IReadOnlyList<DocumentSerial> Serials, string? ParentSerial = null, string? Slot = null, Guid? LotId = null, Guid? ThroughPhantomItemId = null);

public sealed record ComponentIssueDocument(DocumentRef Document, DocumentRef ProductionOrder, DateTime PostedAt, IReadOnlyList<ComponentIssue> Components);

public sealed record TransferDocument(DocumentRef Document, DateTime PostedAt, Guid FromLocationId, string FromLocationKind, Guid ToLocationId, string ToLocationKind, IReadOnlyList<DocumentLine> Lines, bool TwoStep = false, bool IsInvoiced = false);

public sealed record AdjustmentDocument(DocumentRef Document, DateTime PostedAt, string AdjustmentKind, string? ReasonCode, IReadOnlyList<DocumentLine> Lines)
{
    public static class Kinds
    {
        public const string In = "in";
        public const string Out = "out";
        public const string Count = "count";
        public const string StatusChange = "status_change";
    }
}

/// <summary>A shipment (delivery note) to a customer; lines without serials are 3PL lines whose serials come later (case 24).</summary>
public sealed record ShipmentDocument(DocumentRef Document, Guid CustomerPartyRef, DateTime PostedAt, IReadOnlyList<DocumentLine> Lines, Guid? ShipToLocationId = null, Guid? CarrierPartyRef = null, bool IsDropShip = false, DocumentRef? BaseOrder = null, string? CustomerPoRef = null, bool SerialsCopiedWithoutScan = false, bool IsInvoiced = false);

public sealed record DeliveryConfirmation(DocumentRef Document, DocumentRef Shipment, DateTime DeliveredAt, Guid? ShipToLocationId, string Evidence = "carrier_pod", IReadOnlyList<DocumentSerial>? Serials = null);

public static class WarrantyTargetRule
{
    public const string SameOrder = "same_order";
    public const string ReferencedDocument = "referenced_document";
    public const string AttachedList = "attached_list";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([SameOrder, ReferencedDocument, AttachedList], StringComparer.Ordinal);
}

public sealed record WarrantyLineTarget(string Rule, DocumentRef? Document = null, IReadOnlyList<string>? Serials = null, string? Scheme = null, string? RangeStart = null, string? RangeEnd = null);

public sealed record InvoiceLine(int LineNo, Guid ItemId, decimal Quantity, decimal Price, string Currency, string ItemType = "goods", string? PolicyCode = null,
    Guid? PolicyVersionId = null, WarrantyLineTarget? Target = null);

/// <summary>The immutable sales facts needed to apply warranty-service invoice lines.</summary>
public sealed record InvoiceDocument(DocumentRef Document, DocumentRef Order, Guid CustomerPartyRef, Guid ObligorEntityId, DateTime PostedAt,
    IReadOnlyList<InvoiceLine> Lines, string? CustomerPoRef = null, string? Country = null, string? RegionGroup = null, Guid? SiteLocationId = null, bool HasLicensedObligor = false);

/// <summary>Order-entry view of a sales document's targetable goods lines; the sales module remains authoritative for whether each item is serial-tracked.</summary>
public sealed record WarrantyTargetDocument(DocumentRef Document, IReadOnlyList<WarrantyTargetDocumentLine> Lines);

public sealed record WarrantyTargetDocumentLine(int LineNo, Guid ItemId, bool SerialTracked, IReadOnlyList<string> Serials);

public sealed record ReturnDocument(DocumentRef Document, Guid CustomerPartyRef, DateTime ReceivedAt, Guid ReceivingLocationId, IReadOnlyList<DocumentLine> Lines, DocumentRef? BaseDelivery = null, string? ReturnReasonCode = null, string? RmaExternalRef = null);

public sealed record LoanerIssue(DocumentRef Document, Guid CustomerPartyRef, DateTime IssuedAt, Guid? SiteLocationId, DateOnly? DueBack, IReadOnlyList<DocumentLine> Lines, string Reason = "loan");

public interface IPurchasingDocumentQueryPort
{
    Task<ReceiptDocument?> GetReceiptAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<VendorShipment?> GetVendorShipmentAsync(DocumentRef document, CancellationToken cancellationToken);
}

public interface IManufacturingDocumentQueryPort
{
    Task<ProductionReceipt?> GetProductionReceiptAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<ComponentIssueDocument?> GetComponentIssueAsync(DocumentRef document, CancellationToken cancellationToken);
}

/// <summary>
/// Narrow Manufacturing boundary for reservation reassignment. Serialisation owns the reservation; Manufacturing owns
/// the target order's item and serial scheme, so neither module reads the other's tables.
/// </summary>
public interface IProductionOrderAllocationPort
{
    Task<ProductionOrderAllocation?> GetAsync(DocumentRef productionOrder, CancellationToken cancellationToken);
}

public interface IInventoryDocumentQueryPort
{
    Task<TransferDocument?> GetTransferAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<AdjustmentDocument?> GetAdjustmentAsync(DocumentRef document, CancellationToken cancellationToken);
}

public interface ISalesDocumentQueryPort
{
    Task<WarrantyTargetDocument?> GetWarrantyTargetAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<InvoiceDocument?> GetInvoiceAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<ShipmentDocument?> GetShipmentAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<DeliveryConfirmation?> GetDeliveryConfirmationAsync(DocumentRef document, CancellationToken cancellationToken);

    Task<ReturnDocument?> GetReturnAsync(DocumentRef document, CancellationToken cancellationToken);
}

public interface IServiceDocumentQueryPort
{
    Task<LoanerIssue?> GetLoanerIssueAsync(DocumentRef document, CancellationToken cancellationToken);
}
