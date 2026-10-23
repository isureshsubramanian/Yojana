using System.Globalization;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX02;

/// <summary>Translates canonical commands and uses only the remote API and narrow fact/content ports.</summary>
public sealed class Cx02OutboundDocuments(
    Cx02Settings settings,
    Cx02Client client,
    ICx02OutboundFacts facts,
    ICx02AttachmentContent content)
{
    public async Task<OutboundDocument?> FindAsync(OutboundLookup lookup, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(lookup.ConnectionId, cancellationToken);
        return lookup.CommandType switch
        {
            nameof(CreateCreditMemo) => await FindByTextAsync(lookup.ConnectionId, configured, false,
                "salesCreditMemos", "externalDocumentNumber", lookup.ErpReferenceText, cancellationToken),
            nameof(PostWarrantyExpense) => await FindByTextAsync(lookup.ConnectionId, configured, false,
                "journals", "externalDocumentNumber", lookup.ErpReferenceText, cancellationToken),
            nameof(CreateReplacementDelivery) or nameof(ChangeStockStatusOrLocation) =>
                await FindByTextAsync(lookup.ConnectionId, configured, true, "trackedItemLedgerEntries",
                    "documentNumber", lookup.ErpReferenceText, cancellationToken),
            nameof(CreateServiceOrder) => await FindByTextAsync(lookup.ConnectionId, configured, true,
                "serviceItems", "number", lookup.ErpReferenceText, cancellationToken),
            nameof(AttachDocument) when lookup.Payload is not null =>
                await FindAttachmentAsync(lookup, configured, cancellationToken),
            _ => null,
        };
    }

    public async Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var options = configured.Outbound ?? new();
        using var json = JsonDocument.Parse(request.Payload);
        var root = json.RootElement.TryGetProperty("payload", out var nested) ? nested : json.RootElement;
        T Command<T>() => root.Deserialize<T>(IntegrationContracts.Json)
            ?? throw new Cx02OutboundRuleException("The outbound command payload is missing.");
        async Task<Cx02OutboundUnit> Unit(UnitReference reference) =>
            await facts.UnitAsync(request.ConnectionId, reference.UnitId, reference.IdentifierValue,
                cancellationToken) ?? throw new Cx02OutboundRuleException(
                "Map the unit's item and serial before posting this document.");
        async Task<string> Reference(string type, string reference) =>
            await facts.ReferenceAsync(request.ConnectionId, type, reference, cancellationToken)
            ?? throw new Cx02OutboundRuleException($"Map the {type} reference before posting this document.");
        try
        {
            switch (request.CommandType)
            {
                case nameof(CreateCreditMemo):
                    var credit = Command<CreateCreditMemo>();
                    var creditUnits = new List<Cx02OutboundUnit?>();
                    foreach (var line in credit.Lines)
                        creditUnits.Add(line.UnitId is { } id
                            ? await facts.UnitAsync(request.ConnectionId, id,
                                line.Serials is { Count: > 0 } ? line.Serials[0].Value : null,
                                cancellationToken) : null);
                    var mappedCredit = Cx02CreditMapper.Map(credit,
                        await Reference("party", credit.CustomerRef), creditUnits, options);
                    return await CreateCreditAsync(request, configured, mappedCredit, cancellationToken);
                case nameof(PostWarrantyExpense):
                    return await CreateJournalAsync(request, configured,
                        Cx02JournalMapper.Map(Command<PostWarrantyExpense>(), options), cancellationToken);
                case nameof(CreateReplacementDelivery):
                    var delivery = Command<CreateReplacementDelivery>();
                    return await CreateTrackedAsync(request, configured,
                        Cx02TrackedDocumentMapper.Replacement(delivery,
                            await Reference("party", delivery.CustomerRef), await Unit(delivery.ReplacementUnit),
                            options), cancellationToken);
                case nameof(ChangeStockStatusOrLocation):
                    var stock = Command<ChangeStockStatusOrLocation>();
                    return await CreateTrackedAsync(request, configured,
                        Cx02TrackedDocumentMapper.Stock(stock, await Unit(stock.Unit), options), cancellationToken);
                case nameof(CreateServiceOrder):
                    var service = Command<CreateServiceOrder>();
                    return await CreateTrackedAsync(request, configured,
                        Cx02TrackedDocumentMapper.Service(service,
                            await Reference("party", service.CustomerRef), await Unit(service.Unit)),
                        cancellationToken);
                case nameof(AttachDocument):
                    return await AttachAsync(request, configured, Command<AttachDocument>(), cancellationToken);
                default:
                    return OutboundCreateResult.Unsupported(
                        $"CX02 documents mode cannot map command '{request.CommandType}'.");
            }
        }
        catch (JsonException) { return OutboundCreateResult.Rejected("The outbound document payload is invalid."); }
        catch (OutboundAttachmentTooLargeException exception) { return OutboundCreateResult.Rejected(exception.Message); }
        catch (Cx02OutboundRuleException exception) { return OutboundCreateResult.Unsupported(exception.Message); }
        catch (Cx02RemoteException exception) when (exception.IsBusinessRejection)
        {
            return OutboundCreateResult.Rejected(exception.BusinessMessage());
        }
    }

    private async Task<OutboundCreateResult> CreateCreditAsync(OutboundCreateRequest request,
        Cx02ConnectionSettings configured,
        (Dictionary<string, object?> Header, IReadOnlyList<Cx02MappedLine> Lines) mapped,
        CancellationToken cancellationToken)
    {
        var header = await CreateEntityAsync(request.ConnectionId, configured, false, "salesCreditMemos",
            mapped.Header, cancellationToken);
        var documentId = Id(header);
        for (var index = 0; index < mapped.Lines.Count; index++)
        {
            mapped.Lines[index].Body["documentId"] = documentId;
            await CreateEntityAsync(request.ConnectionId, configured, false, "salesCreditMemoLines",
                mapped.Lines[index].Body, cancellationToken);
        }
        if (mapped.Lines.Any(line => !string.IsNullOrWhiteSpace(line.SerialNumber)))
            return await PostTrackedAsync(request, configured, "salesCreditMemo", documentId, mapped.Lines,
                new(), cancellationToken, externalId: "salesCreditMemos:" + documentId);
        await PostStandardAsync(request.ConnectionId, configured, "salesCreditMemos", documentId,
            cancellationToken);
        return OutboundCreateResult.Created("salesCreditMemos:" + documentId, Etag(header));
    }

    private async Task<OutboundCreateResult> CreateJournalAsync(OutboundCreateRequest request,
        Cx02ConnectionSettings configured,
        (Dictionary<string, object?> Header, IReadOnlyList<Dictionary<string, object?>> Lines) mapped,
        CancellationToken cancellationToken)
    {
        var header = await CreateEntityAsync(request.ConnectionId, configured, false, "journals", mapped.Header,
            cancellationToken);
        var documentId = Id(header);
        foreach (var line in mapped.Lines)
        {
            line["journalId"] = documentId;
            await CreateEntityAsync(request.ConnectionId, configured, false, "journalLines", line,
                cancellationToken);
        }
        await PostStandardAsync(request.ConnectionId, configured, "journals", documentId, cancellationToken);
        return OutboundCreateResult.Created("journals:" + documentId, Etag(header));
    }

    private Task<OutboundCreateResult> CreateTrackedAsync(OutboundCreateRequest request,
        Cx02ConnectionSettings configured,
        (string DocumentType, IReadOnlyList<Cx02MappedLine> Lines, Dictionary<string, object?> Metadata) mapped,
        CancellationToken cancellationToken) => PostTrackedAsync(request, configured, mapped.DocumentType,
        DeterministicId.Create(request.OutboundCommandId, "cx02-document"), mapped.Lines, mapped.Metadata,
        cancellationToken);

    private async Task<OutboundCreateResult> PostTrackedAsync(OutboundCreateRequest request,
        Cx02ConnectionSettings configured, string documentType, Guid documentId,
        IReadOnlyList<Cx02MappedLine> lines, Dictionary<string, object?> metadata,
        CancellationToken cancellationToken, string? externalId = null)
    {
        if (lines.Count == 0) throw new Cx02OutboundRuleException("The tracked document has no lines.");
        var tracking = new List<Dictionary<string, object?>>(lines.Count);
        Guid? actionId = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line.SerialNumber)) continue;
            var lineId = DeterministicId.Create(documentId, "line-" + index.ToString(CultureInfo.InvariantCulture));
            var reservationId = DeterministicId.Create(documentId,
                "reservation-" + index.ToString(CultureInfo.InvariantCulture));
            actionId ??= reservationId;
            await CreateEntityAsync(request.ConnectionId, configured, true, "reservationEntries", new()
            {
                ["id"] = reservationId, ["itemNumber"] = line.ItemNumber,
                ["locationCode"] = line.LocationCode, ["quantity"] = line.Quantity,
                ["sourceType"] = documentType, ["sourceId"] = documentId, ["sourceLineNumber"] = (index + 1) * 10,
                ["serialNumber"] = line.SerialNumber, ["lotNumber"] = "", ["packageNumber"] = "",
            }, cancellationToken);
            tracking.Add(new()
            {
                ["lineId"] = lineId, ["quantity"] = line.Quantity, ["serialNumber"] = line.SerialNumber,
                ["lotNumber"] = "", ["packageNumber"] = "",
            });
        }
        if (actionId is null)
            throw new Cx02OutboundRuleException("The extension posting action requires a tracked unit.");
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["documentType"] = documentType,
            ["logicalReference"] = request.ErpReferenceText,
            ["documentId"] = documentId,
            ["tracking"] = tracking,
            ["metadata"] = metadata,
        };
        var uri = EntityUri(configured, true, "reservationEntries", actionId.Value)
            + "/Microsoft.NAV.postTrackedDocument";
        using var result = await client.SendJsonAsync(request.ConnectionId, HttpMethod.Post, new(uri), body,
            idempotent: false, headers: null, cancellationToken);
        var id = result is null ? documentId : Id(result.RootElement);
        var marker = result is null ? null : Text(result.RootElement, "postedAt");
        return OutboundCreateResult.Created(externalId ?? documentType + ":" + id, marker);
    }

    private async Task<OutboundCreateResult> AttachAsync(OutboundCreateRequest request,
        Cx02ConnectionSettings configured, AttachDocument command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ContentRef))
            return OutboundCreateResult.Unsupported("Attachment upload needs a content reference.");
        var options = configured.Outbound ?? new();
        var targetReference = await facts.ReferenceAsync(request.ConnectionId, "document", command.TargetRecordRef,
            cancellationToken) ?? command.TargetRecordRef;
        var target = Cx02AttachmentMapper.Target(targetReference);
        var fileName = Cx02AttachmentMapper.FileName(request.ErpReferenceText, command.DisplayName, options);
        var existing = await FindByTextAsync(request.ConnectionId, configured, false, target.Resource,
            "fileName", fileName, cancellationToken);
        if (existing is not null) return new(OutboundConnectorOutcome.Created, existing);
        var file = await content.ReadAsync(command.ContentRef, options.AttachmentSizeLimit, cancellationToken)
            ?? throw new Cx02OutboundRuleException("Attachment content is unavailable.");
        var metadata = await CreateEntityAsync(request.ConnectionId, configured, false, target.Resource, new()
        {
            ["parentId"] = target.ParentId, ["parentType"] = target.ParentType,
            ["fileName"] = fileName, ["byteSize"] = file.Bytes.LongLength,
        }, cancellationToken);
        var id = Id(metadata);
        await client.SendBytesAsync(request.ConnectionId, HttpMethod.Patch,
            new(EntityUri(configured, false, target.Resource, id) + "/attachmentContent"), file.Bytes,
            file.ContentType, idempotent: true, headers: new Dictionary<string, string> { ["If-Match"] = "*" },
            cancellationToken);
        return OutboundCreateResult.Created(target.Resource + ":" + id, Etag(metadata));
    }

    private async Task<OutboundDocument?> FindAttachmentAsync(OutboundLookup lookup,
        Cx02ConnectionSettings configured, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(lookup.Payload!);
        var root = json.RootElement.TryGetProperty("payload", out var nested) ? nested : json.RootElement;
        var command = root.Deserialize<AttachDocument>(IntegrationContracts.Json);
        if (command is null) return null;
        try
        {
            var targetReference = await facts.ReferenceAsync(lookup.ConnectionId, "document",
                command.TargetRecordRef, cancellationToken) ?? command.TargetRecordRef;
            var target = Cx02AttachmentMapper.Target(targetReference);
            var fileName = Cx02AttachmentMapper.FileName(lookup.ErpReferenceText, command.DisplayName,
                configured.Outbound ?? new());
            return await FindByTextAsync(lookup.ConnectionId, configured, false, target.Resource, "fileName",
                fileName, cancellationToken);
        }
        catch (Cx02OutboundRuleException) { return null; }
    }

    private async Task<OutboundDocument?> FindByTextAsync(Guid connectionId, Cx02ConnectionSettings configured,
        bool custom, string resource, string field, string value, CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString(field + " eq '" + value.Replace("'", "''", StringComparison.Ordinal) + "'");
        var rows = await client.ReadCollectionAsync(connectionId,
            new(CollectionUri(configured, custom, resource) + "?$filter=" + filter + "&$top=1"), 1,
            configured.PageCap, cancellationToken);
        if (rows.Count == 0) return null;
        var row = rows[0];
        return new(resource + ":" + Id(row), Etag(row));
    }

    private async Task<JsonElement> CreateEntityAsync(Guid connectionId, Cx02ConnectionSettings configured,
        bool custom, string resource, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        using var response = await client.SendJsonAsync(connectionId, HttpMethod.Post,
            new(CollectionUri(configured, custom, resource)), body, idempotent: false, headers: null,
            cancellationToken);
        return response?.RootElement.Clone()
            ?? throw new Cx02OutboundRuleException("The remote create response omitted its document.");
    }

    private async Task PostStandardAsync(Guid connectionId, Cx02ConnectionSettings configured, string resource,
        Guid id, CancellationToken cancellationToken)
    {
        using var ignored = await client.SendJsonAsync(connectionId, HttpMethod.Post,
            new(EntityUri(configured, false, resource, id) + "/Microsoft.NAV.post"), new { },
            idempotent: false, headers: null, cancellationToken);
    }

    private static string CollectionUri(Cx02ConnectionSettings configured, bool custom, string resource) =>
        new Uri(custom ? Cx02Protocol.CustomRoot(configured) : Cx02Protocol.StandardRoot(configured),
            $"companies({configured.CompanyId:D})/{resource}").ToString();

    private static string EntityUri(Cx02ConnectionSettings configured, bool custom, string resource, Guid id) =>
        CollectionUri(configured, custom, resource) + $"({id:D})";

    private static Guid Id(JsonElement value) => value.TryGetProperty("id", out var id) && id.TryGetGuid(out var found)
        ? found : throw new Cx02OutboundRuleException("The remote document omitted its identifier.");
    private static string? Etag(JsonElement value) => Text(value, "@odata.etag")
        ?? Text(value, "lastModifiedDateTime");
    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}
