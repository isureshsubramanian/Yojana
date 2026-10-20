using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>Coordinates pure family mappers and narrow remote/content/fact ports. Relay persistence stays in the gateway.</summary>
public sealed class Cx01OutboundDocuments(Cx01Settings settings, Cx01Client client, Cx01DocumentPoster poster,
    ICx01OutboundFacts facts, ICx01AttachmentContent content, ICx01SurveyReader survey, IClock clock, IOutboundProgressStore progress,
    Yojana.Platform.Contracts.Tenancy.ITenantSettings tenantSettings, Yojana.Serialisation.Contracts.ITimeZonePort timeZones)
{
    public async Task<OutboundDocument?> FindAsync(OutboundLookup lookup, CancellationToken ct)
    {
        var configured = await settings.GetAsync(lookup.ConnectionId, ct);
        if (configured.DocumentsAcceptance is null) return null;
        if (lookup.CommandType == nameof(AttachDocument) && lookup.Payload is not null)
        {
            using var json = JsonDocument.Parse(lookup.Payload);
            var root = json.RootElement.TryGetProperty("payload", out var nested) ? nested : json.RootElement;
            var command = root.Deserialize<AttachDocument>(IntegrationContracts.Json)!;
            try
            {
                var name = Cx01AttachmentMapper.FileName(configured.OwnReferencePrefix + lookup.ErpReferenceText,
                    command.DisplayName, configured.Documents ?? new());
                var attachment = await FindAttachmentAsync(lookup.ConnectionId, name, ct);
                if (attachment is null) return null;
                var target = await facts.ReferenceAsync(lookup.ConnectionId, "document", command.TargetRecordRef, ct);
                var (collection, key) = Cx01AttachmentMapper.Target(target ?? command.TargetRecordRef);
                using var linked = await client.GetAsync(lookup.ConnectionId, Cx01Protocol.Root + collection + "(" + key + ")", ct);
                return Cx01ReviewKey.Text(linked.RootElement, "AttachmentEntry") == attachment
                    ? new(Cx01DocumentCatalogue.ExternalId("Attachments2", attachment), OutboundAcknowledgement.Parts(
                        new OutboundDocument(Cx01DocumentCatalogue.ExternalId(collection, key), Cx01Mapper.ChangeMarker(linked.RootElement, key)))) : null;
            }
            catch (Cx01UnmappableException) { return null; }
        }
        var collections = lookup.CommandType switch
        {
            nameof(CreateReplacementDelivery) => new[] { "DeliveryNotes" },
            nameof(CreateCreditMemo) => ["CreditNotes"],
            nameof(PostWarrantyExpense) => ["JournalEntries"],
            nameof(CreateServiceOrder) => (configured.Documents ?? new()).ServiceOrderAction switch
            {
                "return" => ["Returns"], "service_call" => ["ServiceCalls"], _ => Array.Empty<string>(),
            },
            nameof(ChangeStockStatusOrLocation) => ["StockTransfers"],
            _ => Array.Empty<string>(),
        };
        foreach (var collection in collections)
            if (await FindDocumentAsync(lookup.ConnectionId, collection, lookup.ErpReferenceText, configured, ct) is { } found) return found;
        if (lookup.CommandType == nameof(ChangeStockStatusOrLocation))
        {
            var issue = await FindDocumentAsync(lookup.ConnectionId, "InventoryGenExits", lookup.ErpReferenceText, configured, ct);
            var receipt = await FindDocumentAsync(lookup.ConnectionId, "InventoryGenEntries", lookup.ErpReferenceText, configured, ct);
            if (issue is not null && receipt is not null) return receipt with { RelatedDocuments = [issue] };
        }
        // Attachment completion includes a target link. CreateAsync adopts an existing upload then repairs the link;
        // finding an unlinked upload here would cause the relay to acknowledge an incomplete operation.
        return null;
    }

    public async Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request, CancellationToken ct)
    {
        var configured = await settings.GetAsync(request.ConnectionId, ct);
        if (configured.DocumentsAcceptance is null)
            return OutboundCreateResult.Rejected("Documents mode needs recorded customer acceptance in the connection settings.");
        var options = configured.Documents ?? new();
        using var json = JsonDocument.Parse(request.Payload);
        var root = json.RootElement.TryGetProperty("payload", out var nested) ? nested : json.RootElement;
        T Command<T>()
        {
            try { return root.Deserialize<T>(IntegrationContracts.Json)
                ?? throw new Cx01UnmappableException("The outbound command payload is missing."); }
            catch (JsonException) { throw new Cx01DocumentRuleException("The outbound document payload is invalid."); }
        }
        async Task<Cx01OutboundUnit> Unit(Guid id) => await facts.UnitAsync(request.ConnectionId, id, ct)
            ?? throw new Cx01UnmappableException("Map the unit's item and serial before posting this document.");
        async Task<string> Ref(string kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "owner")
                throw new Cx01UnmappableException($"missing {kind} reference in the module event");
            return await facts.ReferenceAsync(request.ConnectionId, kind, value, ct)
                ?? throw new Cx01UnmappableException($"The {kind} reference is not mapped on this connection.");
        }
        Cx01Warehouse Location(string? code) => options.LocationMap?.GetValueOrDefault(code ?? "default")
            ?? throw new Cx01DocumentRuleException($"Map the location '{code ?? "default"}' to a remote warehouse before posting.");
        try
        {
            string collection;
            Dictionary<string, object?> body;
            switch (request.CommandType)
            {
                case nameof(CreateReplacementDelivery):
                    var delivery = Command<CreateReplacementDelivery>();
                    if (string.IsNullOrWhiteSpace(delivery.ShipToRef) || delivery.ShipToRef == "default")
                        throw new Cx01UnmappableException("missing ship-to address name in the module event");
                    var replacement = await Unit(delivery.ReplacementUnit.UnitId);
                    collection = "DeliveryNotes";
                    body = Cx01DeliveryMapper.Map(delivery, await Ref("party", delivery.CustomerRef), replacement,
                        Location(replacement.Location), options, delivery.BaseReturnRef is null ? null : await Ref("document", delivery.BaseReturnRef.Contains(':') ? delivery.BaseReturnRef : "Returns:" + delivery.BaseReturnRef));
                    break;
                case nameof(CreateCreditMemo):
                    var credit = Command<CreateCreditMemo>();
                    if (credit.Lines.Any(x => x.Amount.Amount == 0 && (string.IsNullOrWhiteSpace(x.Amount.Currency) || x.Amount.Currency == "XXX")))
                        throw new Cx01UnmappableException("missing credit amount and currency in the module event");
                    var units = new List<Cx01OutboundUnit?>();
                    foreach (var line in credit.Lines) units.Add(line.UnitId is { } id ? await Unit(id) : null);
                    collection = "CreditNotes";
                    body = Cx01CreditMapper.Map(credit, await Ref("party", credit.CustomerRef), units, Location(null),
                        credit.BaseInvoiceRef is null ? null : await Ref("document", credit.BaseInvoiceRef.Contains(':') ? credit.BaseInvoiceRef : "Invoices:" + credit.BaseInvoiceRef), options);
                    break;
                case nameof(PostWarrantyExpense):
                    collection = "JournalEntries";
                    body = Cx01JournalMapper.Map(Command<PostWarrantyExpense>(), options);
                    break;
                case nameof(CreateServiceOrder):
                    var service = Command<CreateServiceOrder>();
                    var serviceUnit = await Unit(service.Unit.UnitId);
                    var customer = await Ref("party", service.CustomerRef);
                    if (options.ServiceOrderAction == "annotate")
                        return OutboundCreateResult.Unsupported("The service order is configured for reference annotation; no document is created.");
                    collection = options.ServiceOrderAction == "return" ? "Returns" : "ServiceCalls";
                    body = collection == "Returns" ? Cx01ReturnMapper.Map(service, customer, serviceUnit, Location(null), configured)
                        : Cx01ServiceMapper.Map(service, customer, serviceUnit);
                    break;
                case nameof(ChangeStockStatusOrLocation):
                    var stock = Command<ChangeStockStatusOrLocation>();
                    if (string.IsNullOrWhiteSpace(stock.FromLocationOrStatus))
                        throw new Cx01UnmappableException("missing source location in the module event");
                    if (options.LocationMap?.ContainsKey(stock.ToLocationOrStatus) != true && options.StatusPostingPair is null)
                        return OutboundCreateResult.Unsupported("A status-only stock change requires a per-installation test and an enabled goods-issue/goods-receipt pair.");
                    var to = Location(stock.ToLocationOrStatus);
                    var from = Location(stock.FromLocationOrStatus);
                    if (from.Warehouse == to.Warehouse && from.Bin == to.Bin)
                    {
                        if (options.StatusPostingPair is not { } pair)
                            return OutboundCreateResult.Unsupported("A status-only stock change requires a per-installation test and an enabled goods-issue/goods-receipt pair.");
                        var stockUnit = await Unit(stock.Unit.UnitId);
                        var issue = await PostOrFindAsync(request, "InventoryGenExits", Cx01StockMapper.Adjustment(stockUnit, from, pair.IssueAccount), configured, ct);
                        if (issue.Outcome != OutboundConnectorOutcome.Created) return PairWait(issue);
                        var receipt = await PostOrFindAsync(request, "InventoryGenEntries", Cx01StockMapper.Adjustment(stockUnit, to, pair.ReceiptAccount), configured, ct);
                        if (receipt.Outcome is OutboundConnectorOutcome.Rejected or OutboundConnectorOutcome.Unsupported)
                            return receipt with { Reason = PairFailure(issue.Document!, receipt.Reason ?? "receipt could not be posted") };
                        return receipt.Document is null ? PairWait(receipt)
                            : receipt with { Document = receipt.Document with { RelatedDocuments = [issue.Document!] } };
                    }
                    collection = "StockTransfers";
                    body = Cx01StockMapper.Transfer(await Unit(stock.Unit.UnitId), from, to);
                    break;
                case nameof(AttachDocument):
                    return await AttachAsync(request, Command<AttachDocument>(), configured, ct);
                default:
                    return OutboundCreateResult.Unsupported($"CX01 documents mode cannot map command '{request.CommandType}'.");
            }
            return await PostOrFindAsync(request, collection, body, configured, ct);
        }
        catch (Cx01UnmappableException ex) { return OutboundCreateResult.Unsupported(ex.Message); }
        catch (Cx01DocumentRuleException ex) { return OutboundCreateResult.Rejected(ex.Message); }
        catch (Cx01RemoteException ex) when (ex.IsBusinessRejection) { return OutboundCreateResult.Rejected(ex.BusinessMessage()); }
    }

    internal static string PairFailure(OutboundDocument issue, string reason) =>
        $"goods issue {issue.ErpExternalId.Split(':').Last()} posted; receipt rejected: {reason} — post the receipt by hand or reverse the issue";

    internal async Task<OutboundReviewState> PairReviewFailureAsync(OutboundReviewLookup lookup, OutboundReviewState state, CancellationToken ct)
    {
        var configured = await settings.GetAsync(lookup.ConnectionId, ct);
        var issue = await FindDocumentAsync(lookup.ConnectionId, "InventoryGenExits", lookup.ErpReferenceText, configured, ct);
        if (issue is not null)
        {
            await progress.RecordAsync(lookup.ConnectionId, lookup.OutboundCommandId, issue, ct);
            return state with { Reason = PairFailure(issue, state.Reason ?? "receipt review ended") };
        }
        return state;
    }

    internal Task RecordReviewedAsync(OutboundReviewLookup lookup, OutboundDocument document, CancellationToken ct) =>
        progress.RecordAsync(lookup.ConnectionId, lookup.OutboundCommandId, document, ct);

    private static OutboundCreateResult PairWait(OutboundCreateResult result) => result.Outcome == OutboundConnectorOutcome.AwaitingReview
        ? result with { PendingRemoteKey = "pair:" + result.PendingRemoteKey } : result;

    private async Task<OutboundCreateResult> PostOrFindAsync(OutboundCreateRequest request, string collection,
        Dictionary<string, object?> body, Cx01ConnectionSettings configured, CancellationToken ct)
    {
        await Cx01ReferenceRules.CheckAsync(survey, request.ConnectionId, collection, request.ErpReferenceText, configured, ct);
        if (await FindDocumentAsync(request.ConnectionId, collection, request.ErpReferenceText, configured, ct) is { } found)
        {
            await progress.RecordAsync(request.ConnectionId, request.OutboundCommandId, found, ct);
            return new(OutboundConnectorOutcome.Created, found);
        }
        await Cx01MandatoryDefaults.ApplyAsync(survey, request.ConnectionId, collection, body, configured, ct);
        if (collection != "ServiceCalls")
        {
            var zone = await timeZones.ForLegalEntityAsync(Guid.Empty, Yojana.Serialisation.Contracts.TimeZonePurpose.DocumentPresentation, ct) ?? "UTC";
            body.TryAdd(collection == "JournalEntries" ? "ReferenceDate" : "DocDate",
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(zone))
                    .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }
        var result = await poster.PostAsync(request.ConnectionId, collection, body, Cx01DocumentCatalogue.ReferenceField(collection, configured),
            request.ErpReferenceText, ct, qualifyId: true);
        if (result.Document is not null) await progress.RecordAsync(request.ConnectionId, request.OutboundCommandId, result.Document, ct);
        return result;
    }

    private async Task<OutboundDocument?> FindDocumentAsync(Guid connectionId, string collection, string reference,
        Cx01ConnectionSettings configured, CancellationToken ct)
    {
        await Cx01ReferenceRules.CheckAsync(survey, connectionId, collection, reference, configured, ct);
        var field = Cx01DocumentCatalogue.ReferenceField(collection, configured);
        using var response = await client.GetAsync(connectionId, Cx01Protocol.Root + collection + "?$filter="
            + Cx01ReviewKey.FilterEquals(field, configured.OwnReferencePrefix + reference) + "&$top=1", ct);
        // No $select: reads used for capture or serial allocation must retain the serial collections.
        if (response.RootElement.TryGetProperty("value", out var values) && values.GetArrayLength() > 0)
        {
            var row = values[0];
            var key = Cx01ReviewKey.Text(row, Cx01DocumentCatalogue.KeyField(collection))!;
            return await progress.ReadAsync(connectionId, Cx01DocumentCatalogue.ExternalId(collection, key), ct)
                ?? new OutboundDocument(Cx01DocumentCatalogue.ExternalId(collection, key), Cx01Mapper.ChangeMarker(row, key));
        }
        return null;
    }

    private async Task<OutboundCreateResult> AttachAsync(OutboundCreateRequest request, AttachDocument command,
        Cx01ConnectionSettings configured, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.ContentRef)) return OutboundCreateResult.Unsupported("missing attachment content reference in the module event");
        var options = configured.Documents ?? new();
        var sizeLimit = configured.Transport == "site_relay"
            ? Math.Min(options.AttachmentSizeLimit, Yojana.Relay.Contracts.RelayLimits.BodyBytes - 1024L)
            : options.AttachmentSizeLimit;
        var target = await facts.ReferenceAsync(request.ConnectionId, "document", command.TargetRecordRef, ct);
        var (collection, key) = Cx01AttachmentMapper.Target(target ?? command.TargetRecordRef);
        var filename = Cx01AttachmentMapper.FileName(configured.OwnReferencePrefix + request.ErpReferenceText, command.DisplayName, options);
        var attachment = await FindAttachmentAsync(request.ConnectionId, filename, ct);
        var path = Cx01Protocol.Root + collection + "(" + Cx01Protocol.KeyLiteral(key) + ")";
        using var targetDocument = await client.GetAsync(request.ConnectionId, path, ct);
        if (Cx01ReviewKey.Text(targetDocument.RootElement, "AttachmentEntry") is { } existing && existing != "0" && existing != "-1" && existing != attachment)
            return OutboundCreateResult.Rejected("The target already has another attachment group; merge it by hand to preserve existing files.");
        if (attachment is null)
        {
            if (command.ContentRef is null) return OutboundCreateResult.Unsupported("Attachment upload needs a content reference; a URL alone is not uploaded.");
            var file = await content.ReadAsync(command.ContentRef, sizeLimit, ct);
            if (file is null) return OutboundCreateResult.Unsupported("Attachment content is unavailable; configure the attachment content store.");
            if (file.Bytes.LongLength > sizeLimit) return OutboundCreateResult.Rejected("The attachment exceeds the connection's size cap.");
            await tenantSettings.SetAsync(AttachmentIntent(request.ConnectionId, filename), "pending", ct);
            attachment = await poster.UploadAsync(request.ConnectionId, new(filename, file.Bytes, file.ContentType), ct);
            await tenantSettings.SetAsync(AttachmentIntent(request.ConnectionId, filename), attachment, ct);
        }
        var body = new Dictionary<string, object?> { ["AttachmentEntry"] = int.Parse(attachment, System.Globalization.CultureInfo.InvariantCulture) };
        if (collection == "CustomerEquipmentCards" && configured.CertificateUrlField is { } certificate && command.Url is { } url)
            body[certificate] = configured.OwnReferencePrefix + url;
        using var linked = await client.SendJsonAsync(request.ConnectionId, HttpMethod.Patch, path, body, true, ct);
        using var observed = await client.GetAsync(request.ConnectionId, path, ct);
        if (Cx01ReviewKey.Text(observed.RootElement, "AttachmentEntry") != attachment)
            return OutboundCreateResult.Rejected("The target did not retain the attachment link; review its configured posting rules.");
        await progress.RecordAsync(request.ConnectionId, request.OutboundCommandId,
            new(Cx01DocumentCatalogue.ExternalId(collection, key), Cx01Mapper.ChangeMarker(observed.RootElement, key)), ct, mapDocument: false);
        return OutboundCreateResult.Created(Cx01DocumentCatalogue.ExternalId("Attachments2", attachment),
            OutboundAcknowledgement.Parts(new OutboundDocument(Cx01DocumentCatalogue.ExternalId(collection, key),
                Cx01Mapper.ChangeMarker(observed.RootElement, key))));
    }

    private static string AttachmentIntent(Guid connectionId, string filename) => "cx01.upload." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connectionId.ToString("N") + ":" + filename)));

    private async Task<string?> FindAttachmentAsync(Guid connectionId, string filename, CancellationToken ct)
    {
        var stored = await tenantSettings.GetAsync(AttachmentIntent(connectionId, filename), ct);
        if (stored is null) return null;
        if (stored != "pending") return stored;
        // Only an uncertain upload needs recovery. The API has no header filename filter; bound recovery to the newest
        // 100 groups. If absent, require operator resolution instead of scanning the entire company or uploading twice.
        using var page = await client.GetAsync(connectionId, Cx01Protocol.Root + "Attachments2?$orderby=AbsoluteEntry desc&$top=100", ct);
        foreach (var attachment in page.RootElement.GetProperty("value").EnumerateArray())
            if (attachment.TryGetProperty("Attachments2_Lines", out var lines)
                && lines.EnumerateArray().Any(x => Cx01ReviewKey.Text(x, "FileName") + "." + Cx01ReviewKey.Text(x, "FileExtension") == filename))
            {
                var key = Cx01ReviewKey.Text(attachment, "AbsoluteEntry")!;
                await tenantSettings.SetAsync(AttachmentIntent(connectionId, filename), key, ct);
                return key;
            }
        throw new Cx01UnmappableException("The uncertain attachment upload was not among the newest 100 groups; resolve the saved upload intent by hand before retrying.");
    }
}
