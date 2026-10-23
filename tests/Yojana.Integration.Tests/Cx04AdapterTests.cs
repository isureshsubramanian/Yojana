using System.Text.Json;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class Cx04AdapterTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("60000000-0000-0000-0000-000000000006"));
    private static readonly Guid Connection = Guid.Parse("64000000-0000-0000-0000-000000000006");
    private static readonly DateTime Observed = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(Cx04LicenceTier.Essentials, 40)]
    [InlineData(Cx04LicenceTier.Select, 80)]
    [InlineData(Cx04LicenceTier.Prime, 120)]
    [InlineData(Cx04LicenceTier.Enterprise, 240)]
    public void Capability_throttle_stays_below_the_onboarded_tier(Cx04LicenceTier tier, int expected)
    {
        var declaration = Cx04Capabilities.For(Settings(tier));

        Assert.Equal(expected, declaration.Throttle.DefaultProfile.RequestsPerMinute);
        Assert.Equal(1, declaration.Throttle.DefaultProfile.MaxConcurrentCalls);
        Assert.Equal("unsupported", declaration.Messages["CreateServiceOrder"]);
        Assert.Equal("native", declaration.Messages["ProductionCompleted"]);
    }

    [Fact]
    public void Connection_settings_default_to_the_assumed_sliding_ninety_day_chain()
    {
        var settings = Settings(Cx04LicenceTier.Essentials).Validate();

        Assert.Equal(Cx04RefreshExpiryModes.Sliding, settings.RefreshExpiryMode);
        Assert.Equal(90, settings.RefreshLifetimeDays);
        Assert.Equal(Cx04AllocationQuantityMode.LineAndAllocations, settings.AllocationQuantityMode);
    }

    [Theory]
    [InlineData(Cx04AllocationQuantityMode.LineAndAllocations, 5)]
    [InlineData(Cx04AllocationQuantityMode.AllocationsOnly, 2)]
    public void Receipt_quantity_supports_both_documented_readings(Cx04AllocationQuantityMode mode,
        decimal expected)
    {
        using var raw = JsonDocument.Parse("""
            {
              "id":"receipt-id", "ReceiptNbr":"FIC-RCPT-006", "ReceiptDate":"2026-09-01T09:00:00Z",
              "LastModifiedDateTime":"2026-09-01T10:00:00Z", "VendorID":"FIC-SUPPLIER",
              "Details":[{
                "id":"line-id", "LineNbr":1, "InventoryID":"FIC-SERVER", "ReceiptQty":5, "UOM":"EA",
                "Allocations":[
                  {"LotSerialNbr":"FIC-SERIAL-1", "Qty":1, "LocationID":"MAIN"},
                  {"LotSerialNbr":"FIC-SERIAL-2", "Qty":1, "LocationID":"MAIN"}
                ]
              }]
            }
            """);

        var mapped = Cx04Mapper.Map(Tenant, Connection,
            Cx04Protocol.Entities.Values.First(value => value.Resource == "PurchaseReceipt"), raw.RootElement,
            "poll", Observed, mode);
        using var canonical = JsonDocument.Parse(mapped.CanonicalMessage);
        var payload = canonical.RootElement.GetProperty("payload").Deserialize<GoodsReceiptPosted>(
            IntegrationContracts.Json)!;

        Assert.Equal(expected, Assert.Single(payload.Lines).Quantity);
        Assert.Equal(2, payload.Lines.Single().Serials.Count);
    }

    [Fact]
    public void Endpoint_contract_is_pinned_and_modified_field_is_isolated()
    {
        Assert.Equal("24.200.001", Cx04Protocol.EndpointVersion);
        Assert.Equal("LastModified", Cx04Protocol.Entities.Values.First(value => value.Resource == "SalesOrder")
            .ModifiedField);
    }

    private static Cx04ConnectionSettings Settings(Cx04LicenceTier tier) => new(
        new Uri("https://source.invalid/"), "fictional-connected-application",
        new Uri("https://yojana.invalid/callback"), tier);
}
