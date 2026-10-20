using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Connections;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01Warehouse(string Warehouse, int? Bin = null);
public sealed record Cx01StatusPostingPair(string IssueAccount, string ReceiptAccount);
public sealed record Cx01DocumentAcceptance(string AcceptedBy, DateTime AcceptedAt, string Evidence);

public sealed record Cx01DocumentSettings(
    bool ZeroPriceReplacement = true,
    string ServiceOrderAction = "service_call",
    IReadOnlyDictionary<string, string>? AccountMap = null,
    IReadOnlyDictionary<string, Cx01Warehouse>? LocationMap = null,
    Cx01StatusPostingPair? StatusPostingPair = null,
    long AttachmentSizeLimit = 50 * 1024 * 1024,
    IReadOnlyList<string>? AttachmentExtensions = null,
    IReadOnlyDictionary<string, string>? CurrencyMap = null, string? LocalCurrencyCode = null,
    IReadOnlyDictionary<string, int>? CurrencyPrecision = null,
    IReadOnlyDictionary<string, int>? ReferenceFieldLengths = null,
    bool UseRecordedReplacementShipTo = true)
{
    public IReadOnlyList<string> AllowedExtensions => AttachmentExtensions ?? ["pdf", "png", "jpg", "jpeg", "txt", "docx", "xlsx"];

    public void Validate()
    {
        if (CurrencyMap?.Any(x => x.Key.Length != 3 || !x.Key.All(char.IsAsciiLetterUpper) || x.Key == "XXX"
                || string.IsNullOrWhiteSpace(x.Value) || x.Value == "XXX") == true
            || LocalCurrencyCode == "XXX" || CurrencyPrecision?.Values.Any(x => x is < 0 or > 6) == true
            || ReferenceFieldLengths?.Values.Any(x => x < 1) == true)
            throw new InvalidOperationException("The currency map, local currency or field lengths are invalid.");
        if (ServiceOrderAction is not ("service_call" or "return" or "annotate")
            || AttachmentSizeLimit is < 1 or > 50 * 1024 * 1024
            || AllowedExtensions.Any(x => x.Length == 0 || !x.All(char.IsAsciiLetterOrDigit))
            || LocationMap?.Values.Any(x => string.IsNullOrWhiteSpace(x.Warehouse) || x.Bin is < 1) == true
            || AccountMap?.Any(x => string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Value)) == true
            || StatusPostingPair is { } pair && (string.IsNullOrWhiteSpace(pair.IssueAccount)
                || string.IsNullOrWhiteSpace(pair.ReceiptAccount)))
            throw new InvalidOperationException("The CX01 document settings are invalid.");
    }
}

public sealed record UpdateCx01Documents(string OutboundMode, Cx01DocumentSettings Settings, string? Evidence = null, bool? ReadOnly = null);
public sealed record Cx01DocumentsView(string OutboundMode, Cx01DocumentSettings Settings, Cx01DocumentAcceptance? Acceptance, bool ReadOnly);
public sealed class Cx01DocumentRuleException(string message) : InvalidOperationException(message);

public sealed class Cx01DocumentsService(IntegrationDbContext db, ITenantContext tenant, Cx01Settings settings, IClock clock,
    Yojana.Integration.Security.Audit.IConnectionAuditSink audit)
{
    public async Task<Cx01DocumentsView> GetAsync(Guid connectionId, CancellationToken ct)
    {
        var gateway = await GatewayAsync(connectionId, ct);
        var configured = await settings.GetAsync(connectionId, ct);
        return new(gateway.OutboundMode, configured.Documents ?? new(), configured.DocumentsAcceptance, gateway.ReadOnly);
    }

    public async Task<Cx01DocumentsView> SetAsync(Guid connectionId, UpdateCx01Documents request, string actor, CancellationToken ct)
    {
        await using var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, ct);
        var gateway = await GatewayAsync(connectionId, ct);
        if (string.IsNullOrWhiteSpace(actor)) throw new Cx01DocumentRuleException("The authenticated actor is required.");
        if (request.OutboundMode is not ("none" or "reference_only" or "documents"))
            throw new Cx01DocumentRuleException("Outbound mode must be none, reference_only or documents.");
        var configured = await settings.GetAsync(connectionId, ct);
        var evidence = request.Evidence?.Trim();
        if (evidence?.Length > 500) throw new Cx01DocumentRuleException("Acceptance evidence is limited to 500 characters.");
        if (request.OutboundMode == "documents" && configured.DocumentsAcceptance is null && string.IsNullOrWhiteSpace(evidence))
            throw new Cx01DocumentRuleException("Record the customer's acceptance of stock and financial document posting.");
        if (request.OutboundMode == "documents" && string.IsNullOrWhiteSpace(request.Settings.LocalCurrencyCode))
            throw new Cx01DocumentRuleException("Documents mode requires the remote local currency code from the company survey or connection settings.");
        try { request.Settings.Validate(); Cx01ReferenceRules.Validate(configured); }
        catch (InvalidOperationException ex) { throw new Cx01DocumentRuleException(ex.Message); }
        var previous = new { gateway.OutboundMode, gateway.ReadOnly, configured.DocumentsAcceptance };
        // Persist acceptance before enabling the gateway. A failure can leave accepted settings disabled, never enabled without acceptance.
        await settings.SetAsync(connectionId, configured with
        {
            Documents = request.Settings,
            DocumentsAcceptance = request.OutboundMode == "documents" && !string.IsNullOrWhiteSpace(evidence)
                ? new(actor, clock.UtcNow, evidence) : configured.DocumentsAcceptance,
        }, ct);
        gateway.OutboundMode = request.OutboundMode;
        gateway.ReadOnly = request.ReadOnly ?? gateway.ReadOnly;
        var support = Cx01Capabilities.For(configured.Release, configured.WebhooksAvailable, request.OutboundMode).Messages;
        foreach (var row in await db.ConnectionCapabilities.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId).ToListAsync(ct))
            if (support.TryGetValue(row.CanonicalMessage, out var level)) row.SupportLevel = level;
        await db.SaveChangesAsync(ct);
        var current = await settings.GetAsync(connectionId, ct);
        if (previous.OutboundMode != gateway.OutboundMode || previous.ReadOnly != gateway.ReadOnly
            || previous.DocumentsAcceptance != current.DocumentsAcceptance)
            await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(), connectionId,
                Yojana.Integration.Security.Audit.ConnectionAuditEventType.DocumentSettingsChanged, actor, clock.UtcNow,
                System.Text.Json.JsonSerializer.Serialize(new { Before = previous, After = new { gateway.OutboundMode, gateway.ReadOnly, current.DocumentsAcceptance } })), ct);
        return await GetAsync(connectionId, ct);
    }

    private async Task<Yojana.Integration.Data.Entities.ConnectionGatewaySetting> GatewayAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Connections.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id
                && x.ConnectorCode == Cx01Capabilities.ConnectorCode, ct))
            throw new Cx01DocumentRuleException("The CX01 connection was not found.");
        return await db.ConnectionGatewaySettings.SingleAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct);
    }
}

internal static class Cx01DocumentCatalogue
{
    public static IReadOnlyList<string> Collections { get; } =
        ["DeliveryNotes", "CreditNotes", "Returns", "JournalEntries", "StockTransfers", "InventoryGenEntries", "InventoryGenExits", "Attachments2", "ServiceCalls"];
    public static string KeyField(string collection) => collection switch
    {
        "JournalEntries" => "JdtNum", "Attachments2" => "AbsoluteEntry", "ServiceCalls" => "ServiceCallID", _ => "DocEntry",
    };
    public static string ReferenceField(string collection, Cx01ConnectionSettings settings)
    {
        var code = collection switch
        {
            "DeliveryNotes" => "delivery", "CreditNotes" => "credit", "Returns" => "return", "JournalEntries" => "journal",
            "StockTransfers" => "transfer", "InventoryGenEntries" => "receipt", "InventoryGenExits" => "issue", "ServiceCalls" => "service",
            _ => throw new Cx01DocumentRuleException("The document collection has no reference field."),
        };
        return Cx01FieldMap.Effective(settings.FieldMap)["outbound." + code + ".reference"].Field
            ?? throw new Cx01DocumentRuleException($"Configure the outbound reference field for {collection}.");
    }
    public static string ExternalId(string collection, string key) => collection + ":" + key;
}
