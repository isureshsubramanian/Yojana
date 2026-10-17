using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Document query ports fed from memory: the recorded-message harness (P1-T17) and the tests load documents here and
/// publish the thin events; a connector (P8) or an in-house module (P12) replaces these registrations in a real host.
/// </summary>
public sealed class InMemoryDocumentPorts : IPurchasingDocumentQueryPort, IManufacturingDocumentQueryPort, IProductionOrderAllocationPort, IInventoryDocumentQueryPort, ISalesDocumentQueryPort, IOwnershipSourceDocumentQueryPort, IServiceDocumentQueryPort
{
    private readonly ConcurrentDictionary<(string Type, Guid Id), object> documents = new();

    public void Add(ReceiptDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(VendorShipment d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(ProductionReceipt d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(ProductionOrderAllocation d) => documents[(d.ProductionOrder.Type, d.ProductionOrder.Id)] = d;
    public void Add(ComponentIssueDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(TransferDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(AdjustmentDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(ShipmentDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(InvoiceDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(WarrantyTargetDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(DeliveryConfirmation d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(ReturnDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(LoanerIssue d) => documents[(d.Document.Type, d.Document.Id)] = d;

    private Task<T?> Get<T>(DocumentRef r) where T : class => Task.FromResult(documents.TryGetValue((r.Type, r.Id), out var d) ? d as T : null);

    public Task<ReceiptDocument?> GetReceiptAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ReceiptDocument>(document);
    public Task<VendorShipment?> GetVendorShipmentAsync(DocumentRef document, CancellationToken cancellationToken) => Get<VendorShipment>(document);
    public Task<ProductionReceipt?> GetProductionReceiptAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ProductionReceipt>(document);
    public Task<ComponentIssueDocument?> GetComponentIssueAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ComponentIssueDocument>(document);
    public Task<ProductionOrderAllocation?> GetAsync(DocumentRef productionOrder, CancellationToken cancellationToken) => Get<ProductionOrderAllocation>(productionOrder);
    public Task<TransferDocument?> GetTransferAsync(DocumentRef document, CancellationToken cancellationToken) => Get<TransferDocument>(document);
    public Task<AdjustmentDocument?> GetAdjustmentAsync(DocumentRef document, CancellationToken cancellationToken) => Get<AdjustmentDocument>(document);
    public Task<ShipmentDocument?> GetShipmentAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ShipmentDocument>(document);
    public Task<InvoiceDocument?> GetInvoiceAsync(DocumentRef document, CancellationToken cancellationToken) => Get<InvoiceDocument>(document);
    public Task<WarrantyTargetDocument?> GetWarrantyTargetAsync(DocumentRef document, CancellationToken cancellationToken) => Get<WarrantyTargetDocument>(document);
    public Task<DeliveryConfirmation?> GetDeliveryConfirmationAsync(DocumentRef document, CancellationToken cancellationToken) => Get<DeliveryConfirmation>(document);
    public Task<ReturnDocument?> GetReturnAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ReturnDocument>(document);
    public Task<LoanerIssue?> GetLoanerIssueAsync(DocumentRef document, CancellationToken cancellationToken) => Get<LoanerIssue>(document);

    public Task<OwnershipSourceDocument?> GetOwnershipSourceAsync(DocumentRef document, CancellationToken cancellationToken)
    {
        var invoices = documents.Values.OfType<InvoiceDocument>().ToList();
        if (documents.TryGetValue((document.Type, document.Id), out var direct) && direct is InvoiceDocument invoice)
        {
            return Task.FromResult<OwnershipSourceDocument?>(new(document, invoice.CustomerPartyRef, PartyType.Customer,
                invoice.PostedAt, invoice.CustomerPoRef, [invoice]));
        }

        if (direct is ShipmentDocument shipment)
        {
            var warrantyDocuments = invoices.Where(i =>
                    (shipment.BaseOrder is not null && SameDocument(i.Order, shipment.BaseOrder))
                    || i.Lines.Any(l => l.ItemType == "warranty_service" && l.Target?.Document is { } target && SameDocument(target, document)))
                .OrderBy(i => i.PostedAt).ToList();
            return Task.FromResult<OwnershipSourceDocument?>(new(document, shipment.CustomerPartyRef, PartyType.Customer,
                shipment.PostedAt, shipment.CustomerPoRef, warrantyDocuments));
        }

        var byOrder = invoices.Where(i => SameDocument(i.Order, document)).OrderBy(i => i.PostedAt).ToList();
        var owners = byOrder.Select(i => i.CustomerPartyRef).Distinct().ToList();
        if (byOrder.Count == 0 || owners.Count != 1) return Task.FromResult<OwnershipSourceDocument?>(null);
        return Task.FromResult<OwnershipSourceDocument?>(new(document, owners[0], PartyType.Customer,
            byOrder[0].PostedAt, byOrder.Select(i => i.CustomerPoRef).FirstOrDefault(value => value is not null), byOrder));
    }

    private static bool SameDocument(DocumentRef left, DocumentRef right) =>
        left.Type == right.Type && left.Id == right.Id && (left.Line == right.Line || left.Line is null || right.Line is null);
}
