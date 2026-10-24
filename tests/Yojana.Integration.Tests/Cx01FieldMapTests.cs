using System.Text.Json;
using Yojana.Integration.Connectors.CX01;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class Cx01FieldMapTests
{
    private const string Return = """
        {"DocEntry":77,"DocNum":5077,"DocDate":"2026-09-01","CardCode":"C-ACME","DocObjectCode":"oReturns",
         "UpdateDate":"2026-09-01","UpdateTime":"101500",
         "U_YojanaReturnReason":"OURS","U_RetCause":"DOA","U_AuthNo":"RA-9001",
         "DocumentLines":[]}
        """;

    private const string Delivery = """
        {"DocEntry":12,"DocNum":312,"DocDate":"2026-09-01","CardCode":"C-ACME","DocObjectCode":"oDeliveryNotes",
         "UpdateDate":"2026-09-01","UpdateTime":"090000","U_YojanaDropShip":"tYES","U_Direct":"Ja",
         "DocumentLines":[]}
        """;

    [Fact]
    public void Defaults_keep_the_onboarding_fields()
    {
        using var raw = JsonDocument.Parse(Return);
        var payload = Payload(Map("returns", raw.RootElement, null));
        Assert.Equal("OURS", payload.GetProperty("return_reason_code").GetString());
    }

    [Fact]
    public void Customer_fields_and_value_translation_replace_the_defaults()
    {
        using var raw = JsonDocument.Parse(Return);
        var map = new Dictionary<string, Cx01FieldSource>
        {
            [Cx01FieldMap.ReturnReason] = new("U_RetCause",
                ValueMap: new Dictionary<string, string> { ["DOA"] = "dead_on_arrival" }),
            [Cx01FieldMap.ReturnRmaReference] = new("U_AuthNo"),
        };
        var payload = Payload(Map("returns", raw.RootElement, map));
        Assert.Equal("dead_on_arrival", payload.GetProperty("return_reason_code").GetString());
        Assert.Equal("RA-9001", payload.GetProperty("rma_external_ref").GetString());
    }

    [Fact]
    public void Not_used_attribute_reads_nothing_and_yes_values_are_configurable()
    {
        using var raw = JsonDocument.Parse(Delivery);
        var off = new Dictionary<string, Cx01FieldSource> { [Cx01FieldMap.DeliveryDropShip] = new(null) };
        Assert.False(Payload(Map("deliveries", raw.RootElement, off)).GetProperty("is_drop_ship").GetBoolean());
        var custom = new Dictionary<string, Cx01FieldSource>
        {
            [Cx01FieldMap.DeliveryDropShip] = new("U_Direct", TrueValues: ["ja"]),
        };
        Assert.True(Payload(Map("deliveries", raw.RootElement, custom)).GetProperty("is_drop_ship").GetBoolean());
    }

    [Theory]
    [InlineData("item.no_such_thing", "U_X")]
    [InlineData(Cx01FieldMap.ReturnReason, "U_X; DROP")]
    public void Invalid_map_is_rejected_by_the_settings(string code, string field)
    {
        var settings = new Cx01ConnectionSettings(new Uri("https://erp.example.test/"), "FICTIONAL_DEMO", "1000340",
            FieldMap: new Dictionary<string, Cx01FieldSource> { [code] = new(field) });
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void Yes_no_attribute_refuses_a_value_map()
    {
        Assert.Throws<InvalidOperationException>(() => Cx01FieldMap.Validate(new Dictionary<string, Cx01FieldSource>
        {
            [Cx01FieldMap.DeliveryDropShip] = new("U_Direct",
                ValueMap: new Dictionary<string, string> { ["Ja"] = "Y" }),
        }));
    }

    [Fact]
    public void An_inbound_amount_in_the_remote_company_code_reads_back_as_its_iso_currency()
    {
        using var raw = JsonDocument.Parse("""
            {"DocEntry":1291,"DocDate":"2026-10-24T00:00:00Z","UpdateDate":"2026-10-24T00:00:00Z","UpdateTime":"23:03:57",
             "CardCode":"V10000","DocCurrency":"$","DocumentLines":[{"LineNum":0,"ItemCode":"YJ-BAT-P675","Quantity":1,
             "Price":4.5,"WarehouseCode":"01","SerialNumbers":[]}]}
            """);
        var mapped = Cx01Mapper.Map(new TenantId(Guid.NewGuid()), Guid.NewGuid(), Cx01Protocol.Entities["goods_receipts"],
            raw.RootElement, new Dictionary<int, IReadOnlyList<Contracts.CanonicalSerial>>(),
            new Dictionary<int, IReadOnlyList<Contracts.CanonicalSerial>>(), new HashSet<int>(), "poll",
            new DateTime(2026, 10, 24, 17, 0, 0, DateTimeKind.Utc), currencyMap: new Dictionary<string, string> { ["USD"] = "$" });
        Assert.Contains("\"USD\"", mapped.Acceptance.CanonicalMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$\"", mapped.Acceptance.CanonicalMessage, StringComparison.Ordinal);
    }

    private static JsonElement Map(string entity, JsonElement raw, IReadOnlyDictionary<string, Cx01FieldSource>? map)
    {
        var mapped = Cx01Mapper.Map(new TenantId(Guid.NewGuid()), Guid.NewGuid(), Cx01Protocol.Entities[entity],
            raw, new Dictionary<int, IReadOnlyList<Contracts.CanonicalSerial>>(),
            new Dictionary<int, IReadOnlyList<Contracts.CanonicalSerial>>(), new HashSet<int>(), "poll",
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), fields: new Cx01FieldReader(Cx01FieldMap.Effective(map)));
        return JsonDocument.Parse(mapped.Acceptance.CanonicalMessage).RootElement.Clone();
    }

    private static JsonElement Payload(JsonElement canonical) => canonical.GetProperty("payload");
}
