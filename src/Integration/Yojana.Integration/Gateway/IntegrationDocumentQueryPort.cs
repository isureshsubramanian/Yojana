using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using SerialDocumentRef = Yojana.Serialisation.Contracts.DocumentRef;

namespace Yojana.Integration.Gateway;

/// <summary>Canonical-document read adapter consumed only through Serialisation's document query contracts.</summary>
// TIGHT COUPLING (canonical projection visibility)
// WHAT: these reads depend on TranslateInboundMessageHandler remaining NonTransactional and committing the projection
// before it invokes a Serialisation consumed-event handler; the matching declaration is on that handler.
// WHY: this adapter runs in the consumed event's separate message scope and SQL connection. WHAT BREAKS IF SEPARATED:
// the adapter cannot see the uncommitted canonical document and the consumed event is refused as unavailable.
public sealed class IntegrationDocumentQueryPort(IntegrationDbContext db, ITenantContext tenant)
    : IPurchasingDocumentQueryPort, IManufacturingDocumentQueryPort, IInventoryDocumentQueryPort, ISalesDocumentQueryPort
{
    public async Task<ReceiptDocument?> GetReceiptAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row?.MessageType != nameof(GoodsReceiptPosted)) return null;
        var payload = JsonSerializer.Deserialize<GoodsReceiptPosted>(row.Payload, IntegrationContracts.Json)!;
        return new(document, await RefAsync(row.ConnectionId, "party", payload.VendorPartyExternalId, cancellationToken),
            row.OccurredAt, await LinesAsync(row.ConnectionId, payload.Lines, cancellationToken),
            await DocumentRefAsync(row.ConnectionId, payload.BaseOrderRef, cancellationToken), payload.IsAsn);
    }

    public Task<VendorShipment?> GetVendorShipmentAsync(SerialDocumentRef document, CancellationToken cancellationToken) => Task.FromResult<VendorShipment?>(null);

    public async Task<ProductionReceipt?> GetProductionReceiptAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row?.MessageType != nameof(ProductionCompleted)) return null;
        var payload = JsonSerializer.Deserialize<ProductionCompleted>(row.Payload, IntegrationContracts.Json)!;
        var order = await DocumentRefAsync(row.ConnectionId, payload.ProductionOrderRef, cancellationToken)
            ?? throw new InvalidOperationException("production_order_ref_missing");
        var produced = await LinesAsync(row.ConnectionId, payload.Lines, cancellationToken);
        var components = new List<ComponentIssue>();
        foreach (var line in payload.Components)
            components.Add(new(line.LineNo, await RefAsync(row.ConnectionId, "item", line.ItemExternalId, cancellationToken),
                line.Quantity, line.Serials.Select(Serial).ToList(), LotId: null));
        return new(document, order, row.OccurredAt, produced, components);
    }

    public Task<ComponentIssueDocument?> GetComponentIssueAsync(SerialDocumentRef document, CancellationToken cancellationToken) => Task.FromResult<ComponentIssueDocument?>(null);

    public async Task<TransferDocument?> GetTransferAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row?.MessageType != nameof(TransferPosted)) return null;
        var payload = JsonSerializer.Deserialize<TransferPosted>(row.Payload, IntegrationContracts.Json)!;
        return new(document, row.OccurredAt,
            await RefAsync(row.ConnectionId, "location", payload.FromLocationExternalId, cancellationToken), LocationKind.Warehouse,
            await RefAsync(row.ConnectionId, "location", payload.ToLocationExternalId, cancellationToken), LocationKind.Warehouse,
            await LinesAsync(row.ConnectionId, payload.Lines, cancellationToken));
    }

    public async Task<AdjustmentDocument?> GetAdjustmentAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row?.MessageType != nameof(InventoryAdjusted)) return null;
        var payload = JsonSerializer.Deserialize<InventoryAdjusted>(row.Payload, IntegrationContracts.Json)!;
        return new(document, row.OccurredAt, payload.AdjustmentKind.RawValue ?? payload.AdjustmentKind.Value,
            payload.ReasonCode, await LinesAsync(row.ConnectionId, payload.Lines, cancellationToken));
    }

    public async Task<ShipmentDocument?> GetShipmentAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row?.MessageType != nameof(DeliveryPosted)) return null;
        var payload = JsonSerializer.Deserialize<DeliveryPosted>(row.Payload, IntegrationContracts.Json)!;
        return new(document, await RefAsync(row.ConnectionId, "party", payload.CustomerPartyExternalId, cancellationToken),
            row.OccurredAt, await LinesAsync(row.ConnectionId, payload.Lines, cancellationToken),
            await RefAsync(row.ConnectionId, "location", payload.ShipToRef, cancellationToken), IsDropShip: payload.IsDropShip,
            BaseOrder: await DocumentRefAsync(row.ConnectionId, payload.BaseOrderRef, cancellationToken));
    }

    public async Task<InvoiceDocument?> GetInvoiceAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row is null) return null;
        if (row.MessageType == nameof(InvoicePosted))
        {
            var payload = JsonSerializer.Deserialize<InvoicePosted>(row.Payload, IntegrationContracts.Json)!;
            var customer = await RefAsync(row.ConnectionId, "party", payload.CustomerPartyExternalId, cancellationToken);
            var lines = new List<InvoiceLine>();
            foreach (var line in payload.Lines)
            {
                var invoiceItem = await ItemAsync(row.ConnectionId, line.ItemExternalId, cancellationToken);
                var targetReference = line.BaseDocumentRef ?? (payload.BaseDeliveryRefs.Count > 0 ? payload.BaseDeliveryRefs[0] : null);
                var invoiceTarget = invoiceItem.Attributes.IsWarrantyProduct && targetReference is not null
                    ? new WarrantyLineTarget(WarrantyTargetRule.ReferencedDocument,
                        await DocumentRefAsync(row.ConnectionId, targetReference, cancellationToken))
                    : null;
                lines.Add(new(line.LineNo, invoiceItem.ModuleId, line.Quantity,
                    line.Price ?? 0, line.Currency ?? payload.Totals.Currency,
                    invoiceItem.Attributes.IsWarrantyProduct ? "warranty_service" : "goods",
                    invoiceItem.Attributes.WarrantyTemplateRef, Target: invoiceTarget));
            }
            return new(document, document, customer, await LegalEntityAsync(row.ConnectionId, cancellationToken), row.OccurredAt, lines);
        }
        if (row.MessageType != nameof(WarrantyLineSold)) return null;
        var warranty = JsonSerializer.Deserialize<WarrantyLineSold>(row.Payload, IntegrationContracts.Json)!;
        var party = await RefAsync(row.ConnectionId, "party", warranty.CustomerPartyExternalId, cancellationToken);
        var item = await ItemAsync(row.ConnectionId, warranty.WarrantyProductExternalId, cancellationToken);
        var targetDoc = await DocumentRefAsync(row.ConnectionId, warranty.CoveredDeliveryRef, cancellationToken);
        var target = warranty.CoveredUnitSerials.Count > 0
            ? new WarrantyLineTarget(WarrantyTargetRule.AttachedList, Serials: warranty.CoveredUnitSerials)
            : new WarrantyLineTarget(WarrantyTargetRule.ReferencedDocument, targetDoc);
        return new(document, document, party, await LegalEntityAsync(row.ConnectionId, cancellationToken), row.OccurredAt,
            [new InvoiceLine(warranty.Line.LineNo, item.ModuleId, warranty.Line.Quantity, warranty.Price.Amount,
                warranty.Price.Currency, "warranty_service", item.Attributes.WarrantyTemplateRef, Target: target)]);
    }

    public Task<WarrantyTargetDocument?> GetWarrantyTargetAsync(SerialDocumentRef document, CancellationToken cancellationToken) => Task.FromResult<WarrantyTargetDocument?>(null);
    public Task<DeliveryConfirmation?> GetDeliveryConfirmationAsync(SerialDocumentRef document, CancellationToken cancellationToken) => Task.FromResult<DeliveryConfirmation?>(null);

    public async Task<ReturnDocument?> GetReturnAsync(SerialDocumentRef document, CancellationToken cancellationToken)
    {
        var row = await RowAsync(document.Id, cancellationToken);
        if (row?.MessageType != nameof(ReturnReceived)) return null;
        var payload = JsonSerializer.Deserialize<ReturnReceived>(row.Payload, IntegrationContracts.Json)!;
        var location = payload.Lines.Select(x => x.LocationExternalId).FirstOrDefault(x => x is not null) ?? "return";
        return new(document, await RefAsync(row.ConnectionId, "party", payload.CustomerPartyExternalId, cancellationToken),
            row.OccurredAt, await RefAsync(row.ConnectionId, "location", location, cancellationToken),
            await LinesAsync(row.ConnectionId, payload.Lines, cancellationToken),
            await DocumentRefAsync(row.ConnectionId, payload.BaseDeliveryRef, cancellationToken),
            payload.ReturnReasonCode, payload.RmaExternalRef);
    }

    private Task<CanonicalDocument?> RowAsync(Guid id, CancellationToken cancellationToken) =>
        db.CanonicalDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.DocumentId == id, cancellationToken);

    private async Task<Guid> RefAsync(Guid connection, string type, string external, CancellationToken cancellationToken) =>
        await db.ExternalIdMaps.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connection
            && x.EntityType == type && x.ExternalId == external && x.ValidTo == null)
            .Select(x => x.ModuleRef).SingleAsync(cancellationToken);

    private async Task<(Guid ModuleId, ItemReferenceAttributes Attributes)> ItemAsync(Guid connection,
        string external, CancellationToken cancellationToken)
    {
        var moduleId = await RefAsync(connection, "item", external, cancellationToken);
        var json = await db.ReferenceProjections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntityType == "item" && x.ModuleId == moduleId)
            .Select(x => x.AttributesJson).SingleAsync(cancellationToken);
        var attributes = JsonSerializer.Deserialize<ItemReferenceAttributes>(json, IntegrationContracts.Json)
            ?? new(false, null);
        return (moduleId, attributes);
    }

    private async Task<Guid> LegalEntityAsync(Guid connection, CancellationToken cancellationToken) =>
        await db.ConnectionGatewaySettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connection)
            .Select(x => x.LegalEntityId).SingleAsync(cancellationToken);

    private async Task<SerialDocumentRef?> DocumentRefAsync(Guid connection, ExternalDocumentRef? reference, CancellationToken cancellationToken)
    {
        if (reference is null) return null;
        var external = reference.ExternalId ?? reference.Number;
        var id = await RefAsync(connection, "document", external, cancellationToken);
        return new(reference.Type, id, reference.Line);
    }

    private async Task<IReadOnlyList<DocumentLine>> LinesAsync(Guid connection, IReadOnlyList<CanonicalLine> lines, CancellationToken cancellationToken)
    {
        var result = new List<DocumentLine>(lines.Count);
        foreach (var line in lines)
        {
            Guid? location = line.LocationExternalId is null ? null : await RefAsync(connection, "location", line.LocationExternalId, cancellationToken);
            Guid? party = line.PartyExternalId is null ? null : await RefAsync(connection, "party", line.PartyExternalId, cancellationToken);
            result.Add(new(line.LineNo, await RefAsync(connection, "item", line.ItemExternalId, cancellationToken), line.Quantity,
                line.Serials.Select(Serial).ToList(), location, location is null ? null : LocationKind.Warehouse,
                BaseDocument: await DocumentRefAsync(connection, line.BaseDocumentRef, cancellationToken), PartyRef: party,
                ErpLineId: line.ErpLineId, SerialCaptureMethod: SerialCaptureMethodValue.Copied));
        }
        return result;
    }

    private static DocumentSerial Serial(CanonicalSerial serial) => new(serial.Value, ErpSerialId: serial.ErpSerialId);
}
