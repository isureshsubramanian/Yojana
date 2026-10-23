using System.Text.Json;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class Cx02AdapterTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly Guid Connection = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Observed = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Item_mapper_keeps_tracking_and_warranty_facts()
    {
        using var raw = Json(new
        {
            id = Guid.NewGuid(), number = "HW-SERVER-200", displayName = "Fictional edge server",
            itemTrackingCode = "Serial Specific", baseUnitOfMeasureCode = "EA",
            isWarrantyProduct = true, warrantyTemplateRef = "NBD-36", blocked = false,
            lastModifiedDateTime = "2026-09-01T08:00:00Z",
        });

        var mapped = Cx02Mapper.Map(Tenant, Connection, "items", raw.RootElement, "poll", Observed);
        var message = JsonSerializer.Deserialize<CanonicalMessage<ItemUpserted>>(mapped.CanonicalMessage,
            IntegrationContracts.Json)!;

        Assert.Equal(nameof(ItemUpserted), mapped.MessageType);
        Assert.Equal("serial", message.Payload.TrackingMode.Value);
        Assert.True(message.Payload.IsWarrantyProduct);
        Assert.Equal("NBD-36", message.Payload.WarrantyTemplateRef);
    }

    [Fact]
    public void Party_mapper_uses_customer_identity_without_inventing_merge_data()
    {
        using var raw = Json(new
        {
            id = Guid.NewGuid(), number = "C-200", displayName = "Fictional Compute Works",
            taxRegistrationNumber = "FIC-200", countryLetterCode = "IN", blocked = false,
            lastModifiedDateTime = "2026-09-01T08:00:00Z",
        });

        var mapped = Cx02Mapper.Map(Tenant, Connection, "customers", raw.RootElement, "poll", Observed);
        var message = JsonSerializer.Deserialize<CanonicalMessage<PartyUpserted>>(mapped.CanonicalMessage,
            IntegrationContracts.Json)!;

        Assert.Equal("customer", message.Payload.PartyKind.Value);
        Assert.Equal("Fictional Compute Works", message.Payload.LegalName);
        Assert.Null(message.Payload.MergedFromExternalId);
    }

    [Fact]
    public void Invoice_mapper_preserves_base_delivery_serials_and_money()
    {
        using var raw = Json(new
        {
            id = Guid.NewGuid(), number = "INV-200", customerNumber = "C-200", invoiceDate = "2026-09-01",
            totalAmountIncludingTax = 1250m, currencyCode = "INR", lastModifiedDateTime = "2026-09-01T08:00:00Z",
            lines = new[]
            {
                new { id = Guid.NewGuid(), lineNumber = 10, itemNumber = "HW-SERVER-200", quantity = 1m,
                    unitOfMeasureCode = "EA", serials = new[] { "SER-200" }, baseDocumentNumber = "SHIP-200",
                    baseDocumentId = "ship-200", unitPrice = 1250m, currencyCode = "INR" },
            },
        });

        var mapped = Cx02Mapper.Map(Tenant, Connection, "sales_invoices", raw.RootElement, "poll", Observed);
        var message = JsonSerializer.Deserialize<CanonicalMessage<InvoicePosted>>(mapped.CanonicalMessage,
            IntegrationContracts.Json)!;

        Assert.Equal("SHIP-200", Assert.Single(message.Payload.BaseDeliveryRefs).Number);
        Assert.Equal("SER-200", Assert.Single(Assert.Single(message.Payload.Lines).Serials).Value);
        Assert.Equal(1250m, message.Payload.Totals.Amount);
    }

    [Theory]
    [InlineData("Purchase Receipt", nameof(GoodsReceiptPosted))]
    [InlineData("Sales Shipment", nameof(DeliveryPosted))]
    [InlineData("Sales Return Receipt", nameof(ReturnReceived))]
    [InlineData("Transfer Receipt", nameof(TransferPosted))]
    [InlineData("Output", nameof(ProductionCompleted))]
    [InlineData("Positive Adjmt.", nameof(InventoryAdjusted))]
    public void Tracked_ledger_mapper_covers_every_posted_movement(string documentType, string messageType)
    {
        using var raw = Ledger(documentType);

        var mapped = Cx02Mapper.Map(Tenant, Connection, "tracked_ledger_entries", raw.RootElement,
            "poll", Observed, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SER-200"] = "SERVICE-900",
            });

        Assert.Equal(messageType, mapped.MessageType);
        Assert.Contains("SER-200", mapped.CanonicalMessage, StringComparison.Ordinal);
        Assert.Contains("SERVICE-900", mapped.CanonicalMessage, StringComparison.Ordinal);
        Assert.Equal("complete", mapped.Envelope.AttributionCompleteness.Value);
    }

    [Fact]
    public void Credit_memo_maps_to_reversal_of_the_original_invoice_not_a_delete()
    {
        using var raw = Json(new
        {
            id = Guid.NewGuid(), number = "CRM-200", cancelledInvoiceNumber = "INV-200",
            postingDate = "2026-09-02", lastModifiedDateTime = "2026-09-02T08:00:00Z",
        });

        var mapped = Cx02Mapper.Map(Tenant, Connection, "sales_credit_memos", raw.RootElement,
            "poll", Observed);
        var message = JsonSerializer.Deserialize<CanonicalMessage<DocumentCancelled>>(mapped.CanonicalMessage,
            IntegrationContracts.Json)!;

        Assert.Equal("INV-200", message.Payload.DocumentRef.ExternalId);
        Assert.Equal("reversed", message.Payload.CancellationKind.Value);
        Assert.Equal("CRM-200", message.Payload.CancelledByDocumentRef!.Number);
    }

    [Fact]
    public void Notification_parser_verifies_client_state_and_treats_collection_and_business_events_as_hints()
    {
        using var webhook = Json(new
        {
            value = new[]
            {
                new { subscriptionId = Guid.NewGuid(), clientState = "right", changeType = "collection",
                    resource = "/api/yojana/integration/v1.0/companies(x)/trackedItemLedgerEntries?$filter=fictional gt 1",
                    lastModifiedDateTime = "2026-09-01T08:00:00Z" },
                new { subscriptionId = Guid.NewGuid(), clientState = "wrong", changeType = "updated",
                    resource = "/api/yojana/integration/v1.0/companies(x)/serviceItems(1)",
                    lastModifiedDateTime = "2026-09-01T08:00:00Z" },
            },
        });
        using var business = Json(new
        {
            eventName = "salesShipmentPosted", timestamp = "2026-09-01T08:00:00Z",
            clientState = "right", payload = new { documentId = Guid.NewGuid() },
        });

        var collection = Assert.Single(Cx02WebhookEndpoints.Hints(Connection, webhook.RootElement, "right"));
        var businessHint = Assert.Single(Cx02WebhookEndpoints.Hints(Connection, business.RootElement, "right"));

        Assert.Equal("collection", collection.ChangeType);
        Assert.Contains("$filter=fictional gt 1", collection.Resource, StringComparison.Ordinal);
        Assert.Equal("trackedItemLedgerEntries", businessHint.Resource);
    }

    [Fact]
    public void Capability_and_onboarding_profiles_state_the_extension_and_least_privilege_contract()
    {
        Assert.Equal("native", Cx02Capabilities.Declaration.Messages[nameof(InvoicePosted)]);
        Assert.Equal("tenant_code", Cx02Capabilities.Declaration.Messages[nameof(TransferPosted)]);
        Assert.Equal(5, Cx02Capabilities.Declaration.Throttle.DefaultProfile.MaxConcurrentCalls);
        Assert.Equal(1_200, Cx02Capabilities.Declaration.Throttle.DefaultProfile.RequestsPerMinute);
        var profile = new Cx02OnboardingProfile();
        Assert.Contains("never assign the full-access role", profile.IntegrationUser.Detail, StringComparison.Ordinal);
        Assert.Contains("version 1.0 or later", profile.IntegrationUser.Detail, StringComparison.Ordinal);
        Assert.Contains("sandbox", profile.IntegrationUser.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_reject_an_old_extension_and_page_sizes_over_the_protocol_cap()
    {
        var settings = new Cx02ConnectionSettings(new("https://fictional.invalid/"), "directory",
            "tenant", "sandbox", Guid.NewGuid(), "0.9");
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
        Assert.Throws<InvalidOperationException>(() => (settings with
        {
            ExtensionVersion = "1.0", PollPageSize = 20_001,
        }).Validate());
    }

    private static JsonDocument Ledger(string documentType) => Json(new
    {
        id = Guid.NewGuid(), entryNumber = 2001, itemNumber = "HW-SERVER-200", postingDate = "2026-09-01",
        entryType = documentType == "Output" ? "Output" : "Sale", sourceNumber = "C-200",
        documentNumber = "DOC-200", documentType, quantity = documentType == "Sales Shipment" ? -1m : 1m,
        serialNumber = "SER-200", lotNumber = "", packageNumber = "", locationCode = "WH-A",
        lastModifiedDateTime = "2026-09-01T08:00:00Z",
    });

    private static JsonDocument Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value));
}
