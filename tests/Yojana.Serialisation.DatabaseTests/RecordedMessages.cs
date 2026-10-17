using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// The recorded-message harness (P1-T17, ADR-0002 "testable with recorded messages"): a JSON file holds the documents
/// the query ports will answer with and the thin events (plus the module's own commands) in order; the harness loads the
/// documents into the in-memory ports, substitutes the run prefix into every serial, and replays the messages through
/// the bus under one tenant. Later phases add their own files under <c>Recorded/</c> and replay them the same way.
/// </summary>
public sealed class RecordedScenario
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public required string Name { get; init; }

    public string? Description { get; init; }

    public RecordedDocuments Documents { get; init; } = new();

    public List<JsonElement> Messages { get; init; } = [];

    public JsonElement Expect { get; init; }

    public static RecordedScenario Load(string fileName, string prefix)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Recorded", fileName);
        var text = File.ReadAllText(path).Replace("{p}", prefix, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<RecordedScenario>(text, Json) ?? throw new InvalidOperationException($"Cannot read {path}");
    }

    /// <summary>Loads the documents into the ports and replays every message in order; returns one result per message.</summary>
    public async Task<IReadOnlyList<object>> ReplayAsync(SerialisationHostFixture fixture, TenantId tenant, CancellationToken cancellationToken)
    {
        var ports = fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
        foreach (var d in Documents.Receipts) ports.Add(d);
        foreach (var d in Documents.VendorShipments) ports.Add(d);
        foreach (var d in Documents.ProductionReceipts) ports.Add(d);
        foreach (var d in Documents.ComponentIssues) ports.Add(d);
        foreach (var d in Documents.Transfers) ports.Add(d);
        foreach (var d in Documents.Adjustments) ports.Add(d);
        foreach (var d in Documents.Shipments) ports.Add(d);
        foreach (var d in Documents.DeliveryConfirmations) ports.Add(d);
        foreach (var d in Documents.Returns) ports.Add(d);
        foreach (var d in Documents.LoanerIssues) ports.Add(d);

        var results = new List<object>();
        foreach (var message in Messages)
        {
            var type = message.GetProperty("type").GetString()!;
            object command = type switch
            {
                "Purchasing.ReceiptPosted" => new PurchasingReceiptPosted(Envelope(message), Doc(message)),
                "Purchasing.AsnReceived" => new PurchasingAsnReceived(Envelope(message), Doc(message)),
                "Purchasing.VendorShipConfirmed" => new PurchasingVendorShipConfirmed(Envelope(message), Doc(message)),
                "Manufacturing.ProductionReceiptPosted" => new ManufacturingProductionReceiptPosted(Envelope(message), Doc(message)),
                "Manufacturing.ComponentIssued" => new ManufacturingComponentIssued(Envelope(message), Doc(message)),
                "Inventory.TransferPosted" => new InventoryTransferPosted(Envelope(message), Doc(message)),
                "Inventory.AdjustmentPosted" => new InventoryAdjustmentPosted(Envelope(message), Doc(message)),
                "Sales.ShipmentPosted" => new SalesShipmentPosted(Envelope(message), Doc(message)),
                "Sales.DeliveryConfirmed" => new SalesDeliveryConfirmed(Envelope(message), Doc(message)),
                "Sales.ReturnPosted" => new SalesReturnPosted(Envelope(message), Doc(message)),
                "Service.LoanerIssued" => new ServiceLoanerIssued(Envelope(message), Doc(message)),
                "Finance.PeriodClosed" => new FinancePeriodClosed(Envelope(message), message.GetProperty("period").GetString()!),
                "ReleaseUnit" => new ReleaseUnit(Guid.NewGuid(), await UnitBySerialAsync(fixture, tenant, message.GetProperty("serial").GetString()!, cancellationToken), message.GetProperty("eventTime").GetDateTime(), message.GetProperty("actorRef").GetString()!, message.GetProperty("legalEntityId").GetGuid(), message.GetProperty("releaseSignatureRef").GetString()!),
                "CreateEntitlement" => await CreateEntitlementAsync(fixture, tenant, message, cancellationToken),
                "SubmitClaim" => await SubmitClaimAsync(fixture, tenant, message, cancellationToken),
                "AdjudicateClaim" => new AdjudicateClaim(message.GetProperty("commandId").GetGuid(),
                    await ClaimByCommandAsync(fixture, tenant, message.GetProperty("claimCommandId").GetGuid(), cancellationToken),
                    message.GetProperty("eventTime").GetDateTime(), message.GetProperty("actorRef").GetString()!),
                _ => throw new InvalidOperationException($"Recorded message type '{type}' is not known to the harness."),
            };
            using var scope = fixture.ScopeFor(tenant);
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            results.Add(await bus.InvokeAsync<object>(command, new DeliveryOptions { TenantId = tenant.ToString() }, cancellationToken));
        }

        return results;
    }

    public static async Task<Guid> UnitBySerialAsync(SerialisationHostFixture fixture, TenantId tenant, string serial, CancellationToken cancellationToken)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var normalised = serial.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return await db.Identifiers.Where(i => i.NormalisedValue == normalised && i.ValidTo == null).Select(i => i.UnitId).SingleAsync(cancellationToken);
    }

    private static ThinEnvelope Envelope(JsonElement m) => m.GetProperty("envelope").Deserialize<ThinEnvelope>(Json)!;

    private static DocumentRef Doc(JsonElement m) => m.GetProperty("document").Deserialize<DocumentRef>(Json)!;

    private static async Task<CreateEntitlement> CreateEntitlementAsync(SerialisationHostFixture fixture, TenantId tenant,
        JsonElement message, CancellationToken cancellationToken)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var policyCode = message.GetProperty("policyCode").GetString()!;
        var policyVersionId = await db.PolicyVersions.AsNoTracking()
            .Where(p => p.PolicyCode == policyCode && p.Status == PolicyVersionStatus.Published)
            .OrderByDescending(p => p.Version)
            .Select(p => p.PolicyVersionId)
            .FirstAsync(cancellationToken);
        return new CreateEntitlement(message.GetProperty("commandId").GetGuid(),
            await UnitBySerialAsync(fixture, tenant, message.GetProperty("serial").GetString()!, cancellationToken),
            message.GetProperty("coverageFamily").GetString()!, message.GetProperty("contractDate").Deserialize<DateOnly>(Json),
            message.GetProperty("obligorEntityId").GetGuid(), message.GetProperty("eventTime").GetDateTime(),
            message.GetProperty("actorRef").GetString()!,
            message.GetProperty("evidence").Deserialize<Dictionary<string, DateTime>>(Json)!, policyVersionId,
            OwnerPartyRef: message.GetProperty("ownerPartyRef").GetGuid(), OwnerPartyType: message.GetProperty("ownerPartyType").GetString(),
            Country: message.GetProperty("country").GetString());
    }

    private static async Task<SubmitClaim> SubmitClaimAsync(SerialisationHostFixture fixture, TenantId tenant,
        JsonElement message, CancellationToken cancellationToken) => new(
        message.GetProperty("commandId").GetGuid(),
        await UnitBySerialAsync(fixture, tenant, message.GetProperty("serial").GetString()!, cancellationToken), null,
        message.GetProperty("claimDate").Deserialize<DateOnly>(Json), message.GetProperty("claimantPartyRef").GetGuid(),
        message.GetProperty("settlementPartyRef").GetGuid(), message.GetProperty("serviceEntityId").GetGuid(),
        message.GetProperty("obligorEntityId").GetGuid(), message.GetProperty("serviceCountry").GetString()!,
        message.GetProperty("currency").GetString()!, message.GetProperty("lines").Deserialize<List<ClaimLineInput>>(Json)!,
        message.GetProperty("evidence").Deserialize<List<ClaimEvidenceInput>>(Json)!, message.GetProperty("remedy").GetString()!,
        message.GetProperty("consumerChoiceEvidenceRef").GetString()!, message.GetProperty("eventTime").GetDateTime(),
        message.GetProperty("actorRef").GetString()!);

    private static async Task<Guid> ClaimByCommandAsync(SerialisationHostFixture fixture, TenantId tenant, Guid commandId,
        CancellationToken cancellationToken)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.AsNoTracking()
            .Where(c => c.CommandId == commandId).Select(c => c.ClaimId).SingleAsync(cancellationToken);
    }
}

public sealed class RecordedDocuments
{
    public List<ReceiptDocument> Receipts { get; init; } = [];
    public List<VendorShipment> VendorShipments { get; init; } = [];
    public List<ProductionReceipt> ProductionReceipts { get; init; } = [];
    public List<ComponentIssueDocument> ComponentIssues { get; init; } = [];
    public List<TransferDocument> Transfers { get; init; } = [];
    public List<AdjustmentDocument> Adjustments { get; init; } = [];
    public List<ShipmentDocument> Shipments { get; init; } = [];
    public List<DeliveryConfirmation> DeliveryConfirmations { get; init; } = [];
    public List<ReturnDocument> Returns { get; init; } = [];
    public List<LoanerIssue> LoanerIssues { get; init; } = [];
}
