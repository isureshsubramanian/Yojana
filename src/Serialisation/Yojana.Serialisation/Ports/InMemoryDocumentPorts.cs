using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Document query ports fed from memory: the recorded-message harness (P1-T17) and the tests load documents here and
/// publish the thin events; a connector (P8) or an in-house module (P12) replaces these registrations in a real host.
/// </summary>
public sealed class InMemoryDocumentPorts : IPurchasingDocumentQueryPort, IManufacturingDocumentQueryPort, IInventoryDocumentQueryPort, ISalesDocumentQueryPort, IServiceDocumentQueryPort
{
    private readonly ConcurrentDictionary<(string Type, Guid Id), object> documents = new();

    public void Add(ReceiptDocument d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(VendorShipment d) => documents[(d.Document.Type, d.Document.Id)] = d;
    public void Add(ProductionReceipt d) => documents[(d.Document.Type, d.Document.Id)] = d;
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
    public Task<TransferDocument?> GetTransferAsync(DocumentRef document, CancellationToken cancellationToken) => Get<TransferDocument>(document);
    public Task<AdjustmentDocument?> GetAdjustmentAsync(DocumentRef document, CancellationToken cancellationToken) => Get<AdjustmentDocument>(document);
    public Task<ShipmentDocument?> GetShipmentAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ShipmentDocument>(document);
    public Task<InvoiceDocument?> GetInvoiceAsync(DocumentRef document, CancellationToken cancellationToken) => Get<InvoiceDocument>(document);
    public Task<WarrantyTargetDocument?> GetWarrantyTargetAsync(DocumentRef document, CancellationToken cancellationToken) => Get<WarrantyTargetDocument>(document);
    public Task<DeliveryConfirmation?> GetDeliveryConfirmationAsync(DocumentRef document, CancellationToken cancellationToken) => Get<DeliveryConfirmation>(document);
    public Task<ReturnDocument?> GetReturnAsync(DocumentRef document, CancellationToken cancellationToken) => Get<ReturnDocument>(document);
    public Task<LoanerIssue?> GetLoanerIssueAsync(DocumentRef document, CancellationToken cancellationToken) => Get<LoanerIssue>(document);
}
