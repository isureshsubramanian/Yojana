using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04OutboundUnit(string ItemCode, string Serial);

public interface ICx04OutboundFacts
{
    Task<Cx04OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
        CancellationToken ct);
    Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference, CancellationToken ct);
}

internal sealed class Cx04OutboundFacts(IntegrationDbContext db, ITenantContext tenant,
    ICertificateUnitReadPort units) : ICx04OutboundFacts
{
    public async Task<Cx04OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
        CancellationToken ct)
    {
        var unit = await units.ReadAsync(unitId, ct);
        if (unit is null) return null;
        var item = await ReferenceAsync(connectionId, "item", unit.ItemId.ToString(), ct);
        var serials = await units.PrimarySerialsAsync([unitId], ct);
        var serial = string.IsNullOrWhiteSpace(suppliedSerial) ? serials.GetValueOrDefault(unitId) : suppliedSerial;
        return item is null || string.IsNullOrWhiteSpace(serial) ? null : new(item, serial);
    }

    public async Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference,
        CancellationToken ct)
    {
        var candidate = reference.Split(':', 2).Last();
        if (!Guid.TryParse(candidate.Split('/').Last(), out var id))
            return string.IsNullOrWhiteSpace(reference) || reference == "owner" ? null : reference;
        var matches = await db.ExternalIdMaps.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId && value.EntityType == kind && value.ModuleId == id
                && value.ValidTo == null).Select(value => value.ExternalId).Take(2).ToListAsync(ct);
        if (matches.Count > 1) throw new InvalidOperationException("The CX04 remote reference is ambiguous.");
        return matches.FirstOrDefault();
    }
}

public sealed class Cx04OutboundDocuments(Cx04Settings settings, Cx04Client client,
    ICx04OutboundFacts facts, IRenderedOutboundDocumentPort documents) : IOutboundConnector
{
    public string ConnectorCode => Cx04Capabilities.ConnectorCode;
    public OutboundConnectorCapabilities Capabilities { get; } = new(false, OwnsConnectionLane: true);

    public async Task<OutboundDocument?> FindByReferenceAsync(OutboundLookup lookup, CancellationToken cancellationToken)
    {
        if (lookup.CommandType == nameof(AttachDocument)) return null;
        var configured = await settings.GetAsync(lookup.ConnectionId, cancellationToken);
        var entity = Entity(lookup.CommandType);
        var filter = "$filter=" + Uri.EscapeDataString("ExternalRef eq '"
            + lookup.ErpReferenceText.Replace("'", "''", StringComparison.Ordinal) + "'");
        var rows = await client.ReadCollectionAsync(lookup.ConnectionId,
            new Uri(Cx04Protocol.Collection(configured, entity) + "?" + filter), 2, 2, cancellationToken);
        if (rows.Count > 1) throw new InvalidOperationException("The CX04 outbound reference is not unique.");
        return rows.SingleOrDefault() is { } row ? Document(entity, row) : null;
    }

    public async Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.CommandType switch
            {
                nameof(CreateReplacementDelivery) => await CreateReplacementAsync(request, cancellationToken),
                nameof(CreateCreditMemo) => await CreateCreditAsync(request, cancellationToken),
                nameof(PostWarrantyExpense) => await CreateExpenseAsync(request, cancellationToken),
                nameof(ChangeStockStatusOrLocation) => await CreateStockMoveAsync(request, cancellationToken),
                nameof(AttachDocument) => await AttachAsync(request, cancellationToken),
                nameof(CreateServiceOrder) => OutboundCreateResult.Unsupported(
                    "The service-order surface is not verified for CX04."),
                _ => OutboundCreateResult.Unsupported($"CX04 cannot map command '{request.CommandType}'."),
            };
        }
        catch (Cx04RemoteException exception) when (exception.IsRejected)
        {
            return OutboundCreateResult.Rejected("The remote system rejected the mapped document.");
        }
        catch (InvalidOperationException exception)
        {
            return OutboundCreateResult.Rejected(exception.Message);
        }
    }

    private async Task<OutboundCreateResult> CreateReplacementAsync(OutboundCreateRequest request,
        CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<CreateReplacementDelivery>(request.Payload,
            IntegrationContracts.Json) ?? throw new InvalidOperationException("The replacement payload is invalid.");
        var unit = await facts.UnitAsync(request.ConnectionId, command.ReplacementUnit.UnitId,
            command.ReplacementUnit.IdentifierValue, ct)
            ?? throw new InvalidOperationException("The replacement unit has no unambiguous remote item and serial mapping.");
        var configured = await settings.GetAsync(request.ConnectionId, ct);
        var detail = SerialLine(unit, 1, configured.AllocationQuantityMode);
        var body = new Dictionary<string, object?>
        {
            ["ExternalRef"] = Wrapped(request.ErpReferenceText),
            ["CustomerID"] = Wrapped(command.CustomerRef), ["ShipToID"] = Wrapped(command.ShipToRef),
            ["Description"] = Wrapped(command.ReasonCodeText), ["Details"] = new[] { detail },
        };
        return await PutAsync(request, "shipments", body, ct);
    }

    private async Task<OutboundCreateResult> CreateCreditAsync(OutboundCreateRequest request,
        CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<CreateCreditMemo>(request.Payload, IntegrationContracts.Json)
            ?? throw new InvalidOperationException("The credit payload is invalid.");
        var configured = await settings.GetAsync(request.ConnectionId, ct);
        var details = command.Lines.Select((line, index) =>
        {
            var detail = new Dictionary<string, object?>
            {
                ["LineNbr"] = Wrapped(index + 1), ["InventoryID"] = Wrapped(line.ItemExternalId ?? "NONSTOCK"),
                ["UnitPrice"] = Wrapped(line.Amount.Amount), ["Description"] = Wrapped(line.Description),
                ["Allocations"] = line.Serials?.Select(serial => new Dictionary<string, object?>
                {
                    ["LotSerialNbr"] = Wrapped(serial.Value), ["Qty"] = Wrapped(1m),
                }).ToArray() ?? [],
            };
            if (configured.AllocationQuantityMode == Cx04AllocationQuantityMode.LineAndAllocations
                || line.Serials is null or { Count: 0 }) detail["Qty"] = Wrapped(line.Quantity);
            return detail;
        }).ToArray();
        var body = new Dictionary<string, object?>
        {
            ["Type"] = Wrapped("CM"), ["ExternalRef"] = Wrapped(request.ErpReferenceText),
            ["CustomerID"] = Wrapped(command.CustomerRef), ["Details"] = details,
        };
        return await PutAsync(request, "returns", body, ct);
    }

    private async Task<OutboundCreateResult> CreateExpenseAsync(OutboundCreateRequest request,
        CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<PostWarrantyExpense>(request.Payload, IntegrationContracts.Json)
            ?? throw new InvalidOperationException("The expense payload is invalid.");
        if (command.JournalLines.Count == 0)
            throw new InvalidOperationException("The warranty expense has no journal lines.");
        var body = new Dictionary<string, object?>
        {
            ["ExternalRef"] = Wrapped(request.ErpReferenceText),
            ["Description"] = Wrapped(command.CohortTag ?? "Warranty expense"),
            ["Details"] = command.JournalLines.Select((line, index) => new Dictionary<string, object?>
            {
                ["LineNbr"] = Wrapped(index + 1), ["Account"] = Wrapped(line.AccountRef),
                ["DebitAmount"] = Wrapped(line.Debit), ["CreditAmount"] = Wrapped(line.Credit),
                ["CurrencyID"] = Wrapped(line.Currency),
            }).ToArray(),
        };
        return await PutAsync(request, new Cx04EntityDefinition("journal", "Default", "JournalTransaction",
            nameof(PostWarrantyExpense), "ReferenceNbr", "LastModifiedDateTime"), body, ct);
    }

    private async Task<OutboundCreateResult> CreateStockMoveAsync(OutboundCreateRequest request,
        CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ChangeStockStatusOrLocation>(request.Payload,
            IntegrationContracts.Json) ?? throw new InvalidOperationException("The stock-move payload is invalid.");
        var unit = await facts.UnitAsync(request.ConnectionId, command.Unit.UnitId, command.Unit.IdentifierValue, ct)
            ?? throw new InvalidOperationException("The stock-move unit has no unambiguous remote item and serial mapping.");
        var configured = await settings.GetAsync(request.ConnectionId, ct);
        var line = SerialLine(unit, 1, configured.AllocationQuantityMode);
        line["FromLocationID"] = Wrapped(command.FromLocationOrStatus ?? "");
        line["LocationID"] = Wrapped(command.ToLocationOrStatus);
        var body = new Dictionary<string, object?>
        {
            ["ExternalRef"] = Wrapped(request.ErpReferenceText), ["Description"] = Wrapped(command.Reason),
            ["Details"] = new[] { line },
        };
        return await PutAsync(request, new Cx04EntityDefinition("stock_moves", "Default", "InventoryTransfer",
            nameof(ChangeStockStatusOrLocation), "ReferenceNbr", "LastModifiedDateTime"), body, ct);
    }

    private async Task<OutboundCreateResult> AttachAsync(OutboundCreateRequest request, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<AttachDocument>(request.Payload, IntegrationContracts.Json)
            ?? throw new InvalidOperationException("The attachment payload is invalid.");
        if (!OutboundDocumentContentReference.TryDocumentId(command.ContentRef, out var documentId))
            throw new InvalidOperationException("The attachment content is unavailable.");
        var content = await documents.ReadAsync(documentId, ct)
            ?? throw new InvalidOperationException("The attachment content is unavailable.");
        var configured = await settings.GetAsync(request.ConnectionId, ct);
        if (content.Bytes.Length > configured.AttachmentSizeLimitBytes)
            throw new InvalidOperationException("The attachment exceeds the connection's configured size cap.");
        var target = command.TargetRecordRef.Split(':', 2);
        if (target.Length != 2) throw new InvalidOperationException("The attachment target must name a CX04 entity and key.");
        var entity = Cx04Protocol.Entity(target[0]);
        using var row = await client.GetAsync(request.ConnectionId,
            Cx04Protocol.Record(configured, entity, target[1]), ct);
        var href = row.RootElement.GetProperty("_links").GetProperty("files:put").GetProperty("href").GetString()
            ?? throw new InvalidOperationException("The remote record does not expose file upload.");
        var fileName = Uri.EscapeDataString(content.FileName.Replace("{", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal));
        var location = await client.PutFileAsync(request.ConnectionId, new(href.Replace("{filename}", fileName,
            StringComparison.Ordinal)), content.Bytes, request.ErpReferenceText, ct);
        return OutboundCreateResult.Created(location?.ToString() ?? request.ErpReferenceText);
    }

    private async Task<OutboundCreateResult> PutAsync(OutboundCreateRequest request, string entity,
        Dictionary<string, object?> body, CancellationToken ct) =>
        await PutAsync(request, Cx04Protocol.Entity(entity), body, ct);

    private async Task<OutboundCreateResult> PutAsync(OutboundCreateRequest request, Cx04EntityDefinition entity,
        Dictionary<string, object?> body, CancellationToken ct)
    {
        var configured = await settings.GetAsync(request.ConnectionId, ct);
        using var result = await client.PutAsync(request.ConnectionId, Cx04Protocol.Collection(configured, entity),
            body, createOnly: true, ct)
            ?? throw new Cx04RemoteException(System.Net.HttpStatusCode.OK, "document create");
        var document = Document(entity, result.RootElement);
        return OutboundCreateResult.Created(document.ErpExternalId, document.ErpChangeMarker);
    }

    private static Dictionary<string, object?> SerialLine(Cx04OutboundUnit unit, decimal quantity,
        Cx04AllocationQuantityMode quantityMode)
    {
        var line = new Dictionary<string, object?>
        {
            ["InventoryID"] = Wrapped(unit.ItemCode),
            ["Allocations"] = new[] { new Dictionary<string, object?>
            {
                ["LotSerialNbr"] = Wrapped(unit.Serial), ["Qty"] = Wrapped(quantity),
            } },
        };
        // A1 is a per-connection acceptance choice: documented examples use both values; the other reading omits
        // the line quantity because some remote releases reportedly double it.
        if (quantityMode == Cx04AllocationQuantityMode.LineAndAllocations) line["Qty"] = Wrapped(quantity);
        return line;
    }

    private static Cx04EntityDefinition Entity(string commandType) => commandType switch
    {
        nameof(CreateReplacementDelivery) => Cx04Protocol.Entity("shipments"),
        nameof(CreateCreditMemo) => Cx04Protocol.Entity("returns"),
        nameof(PostWarrantyExpense) => new("journal", "Default", "JournalTransaction", commandType,
            "ReferenceNbr", "LastModifiedDateTime"),
        nameof(ChangeStockStatusOrLocation) => new("stock_moves", "Default", "InventoryTransfer", commandType,
            "ReferenceNbr", "LastModifiedDateTime"),
        _ => throw new InvalidOperationException("The CX04 outbound command type is not queryable."),
    };

    private static OutboundDocument Document(Cx04EntityDefinition entity, JsonElement row) => new(
        Cx04Mapper.Text(row, entity.KeyField) ?? Cx04Mapper.Text(row, "id")
            ?? throw new InvalidOperationException("The CX04 document response has no identity."),
        Cx04Mapper.Text(row, entity.ModifiedField) ?? Cx04Mapper.Text(row, "rowNumber"));

    private static Dictionary<string, object?> Wrapped(object? value) => new() { ["value"] = value };
}
