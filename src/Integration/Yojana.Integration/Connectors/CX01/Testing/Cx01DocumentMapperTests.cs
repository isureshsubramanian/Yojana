using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX01.Testing;

public sealed class Cx01DocumentMapperTests
{
    private static void Golden(string expected, object actual)
    {
        using var wanted = JsonDocument.Parse(expected);
        var found = JsonSerializer.SerializeToElement(actual, Cx01Protocol.Json);
        Assert.True(JsonElement.DeepEquals(wanted.RootElement, found), found.ToString());
    }

    [Fact]
    public void Delivery_zero_price_and_base_return_have_the_expected_body()
    {
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "ref");
        Golden("""{"CardCode":"C1","ShipToCode":"SHIP","DocumentLines":[{"ItemCode":"I1","Quantity":1,"WarehouseCode":"01","UnitPrice":0,"BaseType":16,"BaseEntry":12,"BaseLine":3,"SerialNumbers":[{"InternalSerialNumber":"S1","SystemSerialNumber":17,"Quantity":1}]}]}""",
            Cx01DeliveryMapper.Map(command with { BaseReturnLine = 3 }, "C1", new("I1", "S1", "17"), new("01"), new(), "12"));
    }

    [Fact]
    public void Priced_delivery_requires_a_price_and_carries_its_currency()
    {
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "ref");
        Assert.Throws<Cx01UnmappableException>(() => Cx01DeliveryMapper.Map(command, "C", Cx01DocumentScenarios.Unit, new("01"), new(ZeroPriceReplacement: false), null));
        var body = Cx01DeliveryMapper.Map(command with { PricePolicy = OpenEnum.Known("full"), Price = new(42, "USD") }, "C", new("I1", "S1"), new("01"), Cx01DocumentScenarios.Options with { ZeroPriceReplacement = false, CurrencyMap = new Dictionary<string, string> { ["USD"] = "USD" } }, null);
        Golden("""{"CardCode":"C","ShipToCode":"SHIP","DocumentLines":[{"ItemCode":"I1","Quantity":1,"WarehouseCode":"01","UnitPrice":42,"Currency":"USD","SerialNumbers":[{"InternalSerialNumber":"S1","Quantity":1}]}]}""", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Credit_serials_and_optional_invoice_link_have_the_expected_body(bool linked)
    {
        var command = (CreateCreditMemo)Cx01DocumentScenarios.Command("credit", "ref");
        var body = Cx01CreditMapper.Map(command, "C1", [new("I1", "S1", "17")], new("01"), linked ? "13" : null, Cx01DocumentScenarios.Options);
        var link = linked ? ",\"BaseType\":13,\"BaseEntry\":13,\"BaseLine\":0" : "";
        Golden("{\"CardCode\":\"C1\",\"DocumentLines\":[{\"ItemCode\":\"I1\",\"Quantity\":1,\"WarehouseCode\":\"01\",\"ItemDescription\":\"Fictional credit\",\"LineTotal\":25,\"SerialNumbers\":[{\"InternalSerialNumber\":\"S1\",\"SystemSerialNumber\":17,\"Quantity\":1}]" + link + "}]}", body);
        Assert.Throws<Cx01UnmappableException>(() => Cx01CreditMapper.Map(command with { Lines = [command.Lines[0] with { BaseLine = null }] }, "C1", [Cx01DocumentScenarios.Unit], new("01"), "13", Cx01DocumentScenarios.Options));
    }

    [Fact]
    public void Return_keeps_the_mapped_rma_annotation_and_serials()
    {
        var command = (CreateServiceOrder)Cx01DocumentScenarios.Command("return", "RMA1");
        var settings = new Cx01ConnectionSettings(new("https://fictional.example.test"), "FIC", "test", FieldMap:
            new Dictionary<string, Cx01FieldSource> { [Cx01FieldMap.ReturnRmaReference] = new("U_FicRma") });
        Golden("""{"CardCode":"C1","U_FicRma":"YOJANA:RMA1","DocumentLines":[{"ItemCode":"I1","Quantity":1,"WarehouseCode":"01","SerialNumbers":[{"InternalSerialNumber":"S1","Quantity":1}]}]}""",
            Cx01ReturnMapper.Map(command, "C1", new("I1", "S1"), new("01"), settings));
        var disabled = settings with { RmaReferenceField = "U_Legacy", FieldMap =
            new Dictionary<string, Cx01FieldSource> { [Cx01FieldMap.ReturnRmaReference] = new(null) } };
        Assert.DoesNotContain("U_Legacy", Cx01ReturnMapper.Map(command, "C1", new("I1", "S1"), new("01"), disabled).Keys);
    }

    [Fact]
    public void Journal_maps_accounts_and_refuses_unmapped_or_unbalanced_lines()
    {
        var command = (PostWarrantyExpense)Cx01DocumentScenarios.Command("journal", "ref");
        Golden("""{"JournalEntryLines":[{"AccountCode":"6100","Debit":25,"Credit":0},{"AccountCode":"2100","Debit":0,"Credit":25}],"Reference2":null}""", Cx01JournalMapper.Map(command, Cx01DocumentScenarios.Options));
        Assert.Throws<Cx01UnmappableException>(() => Cx01JournalMapper.Map(command, new()));
        Assert.Throws<Cx01DocumentRuleException>(() => Cx01JournalMapper.Map(command with { JournalLines = [command.JournalLines[0]] }, Cx01DocumentScenarios.Options));
    }

    [Fact]
    public void Stock_transfer_carries_both_bin_directions_and_serial_index()
    {
        Golden("""{"FromWarehouse":"01","ToWarehouse":"02","StockTransferLines":[{"ItemCode":"I1","Quantity":1,"FromWarehouseCode":"01","WarehouseCode":"02","SerialNumbers":[{"InternalSerialNumber":"S1","SystemSerialNumber":17,"Quantity":1}],"StockTransferLinesBinAllocations":[{"BinAbsEntry":1,"Quantity":1,"SerialAndBatchNumbersBaseLine":0,"BinActionType":"batFromWarehouse"},{"BinAbsEntry":2,"Quantity":1,"SerialAndBatchNumbersBaseLine":0,"BinActionType":"batToWarehouse"}]}]}""",
            Cx01StockMapper.Transfer(new("I1", "S1", "17"), new("01", 1), new("02", 2)));
        Golden("""{"DocumentLines":[{"ItemCode":"I1","Quantity":1,"WarehouseCode":"01","AccountCode":"6100","SerialNumbers":[{"InternalSerialNumber":"S1","Quantity":1}]}]}""",
            Cx01StockMapper.Adjustment(new("I1", "S1"), new("01"), "6100"));
    }

    [Fact]
    public void Service_call_identifies_the_installed_serial_without_stock_allocation()
    {
        Golden("""{"CustomerCode":"C1","Subject":"Fictional repair","ItemCode":"I1","InternalSerialNum":"S1"}""",
            Cx01ServiceMapper.Map((CreateServiceOrder)Cx01DocumentScenarios.Command("service", "ref"), "C1", new("I1", "S1")));
    }

    [Fact]
    public async Task Attachment_has_a_deterministic_reference_filename_and_multipart_shape()
    {
        var name = Cx01AttachmentMapper.FileName("ref", "certificate.pdf", new());
        Assert.Equal(name, Cx01AttachmentMapper.FileName("ref", "renamed.pdf", new()));
        Assert.NotEqual(name, Cx01AttachmentMapper.FileName("other", "certificate.pdf", new()));
        using var content = new Cx01MultipartUpload(name, [1, 2, 3], "application/pdf").CreateContent();
        var wire = await content.ReadAsStringAsync();
        // Live shape rule: disposition before content type, no filename* parameter, unquoted boundary.
        Assert.Contains($"Content-Disposition: form-data; name=\"files\"; filename=\"{name}\"\r\nContent-Type: application/pdf", wire);
        Assert.DoesNotContain("filename*", wire);
        Assert.DoesNotContain("boundary=\"", string.Join(";", content.Headers.GetValues("Content-Type")));
        Assert.Throws<Cx01DocumentRuleException>(() => Cx01AttachmentMapper.FileName("ref", "run.exe", new()));
        Assert.Throws<Cx01UnmappableException>(() => Cx01AttachmentMapper.Target("Arbitrary:1"));
    }

    [Fact]
    public void Relay_allows_exact_document_collections_and_only_the_link_patches()
    {
        var declaration = Cx01RelayProfile.Declaration(new(new("https://fictional.example.test"), "FIC", "test", DocumentsAcceptance: new("actor", new(2026, 9, 1), "accepted")), "documents");
        bool Allowed(string method, string path) => declaration.Paths.Any(x => x.Method == method && Regex.IsMatch(path, "\\A(?:" + x.Pattern + ")\\z"));
        foreach (var collection in Cx01DocumentCatalogue.Collections)
        {
            Assert.True(Allowed("POST", Cx01Protocol.Root + collection), collection);
            Assert.True(Allowed("GET", Cx01Protocol.Root + collection), collection);
            Assert.True(Allowed("GET", Cx01Protocol.Root + collection + "(8001)"), collection);
            Assert.False(Allowed("POST", Cx01Protocol.Root + collection + "(8001)"));
            Assert.False(Allowed("DELETE", Cx01Protocol.Root + collection + "(8001)"));
            Assert.False(Allowed("PATCH", Cx01Protocol.Root + collection));
            Assert.Equal(Cx01AttachmentMapper.LinkCollections.Contains(collection), Allowed("PATCH", Cx01Protocol.Root + collection + "(8001)"));
        }
        foreach (var collection in Cx01AttachmentMapper.LinkCollections) Assert.True(Allowed("PATCH", Cx01Protocol.Root + collection + "(8001)"));
        foreach (var collection in new[] { "Orders", "Invoices", "PurchaseReturns", "BusinessPartners", "Items" })
            Assert.False(Allowed("POST", Cx01Protocol.Root + collection));
        Assert.False(Allowed("POST", Cx01Protocol.Root + "Returns(1)/Cancel"));
        Assert.True(Allowed("GET", Cx01Protocol.Root + "SerialNumberDetails"));
        Assert.False(Allowed("POST", Cx01Protocol.Root + "SerialNumberDetails"));
    }

    [Fact]
    public void Composite_acknowledgement_keeps_each_exact_marker_within_the_existing_column()
    {
        var receipt = new OutboundDocument("InventoryGenEntries:1", "2026-09-01T100000|1", [new("InventoryGenExits:1", "2026-09-01T100001|1")]);
        var marker = OutboundAcknowledgement.Marker(receipt)!;
        Assert.True(marker.Length < 320);
        Assert.Contains(OutboundAcknowledgement.Token(receipt.ErpExternalId, receipt.ErpChangeMarker!), marker);
        Assert.DoesNotContain(OutboundAcknowledgement.Token(receipt.ErpExternalId, "later"), marker);
    }
}
