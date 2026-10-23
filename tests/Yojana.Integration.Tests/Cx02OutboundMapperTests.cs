using Yojana.Integration.Contracts;
using Xunit;

namespace Yojana.Integration.Connectors.CX02;

public sealed class Cx02OutboundMapperTests
{
    private static readonly Guid Unit = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Cx02OutboundUnit RemoteUnit = new("HW-SERVER-300", "FIC-SERIAL-300", "WH-A");
    private static readonly Cx02OutboundSettings Settings = new(
        AccountMap: new Dictionary<string, string> { ["warranty"] = "FIC-6100", ["reserve"] = "FIC-2100" },
        LocationMap: new Dictionary<string, string> { ["store"] = "WH-A", ["repair"] = "WH-R" });

    [Fact]
    public void Credit_mapper_uses_the_native_header_and_tracked_line_shape()
    {
        var command = new CreateCreditMemo(Guid.NewGuid(), "document/credit", "FIC-CREDIT-300",
            "customer/300", [new(Unit, "Fictional rack server", 1, new(125, "GBP"),
                Serials: [new("FIC-SERIAL-300")])], null, "FIC-INVOICE-300");

        var mapped = Cx02CreditMapper.Map(command, "FIC-CUSTOMER-300", [RemoteUnit], Settings);

        Assert.Equal("FIC-CUSTOMER-300", mapped.Header["customerNumber"]);
        Assert.Equal("FIC-CREDIT-300", mapped.Header["externalDocumentNumber"]);
        Assert.Equal("HW-SERVER-300", Assert.Single(mapped.Lines).ItemNumber);
        Assert.Equal("FIC-SERIAL-300", Assert.Single(mapped.Lines).SerialNumber);
    }

    [Fact]
    public void Journal_mapper_applies_tenant_account_mappings_and_requires_balance()
    {
        var command = new PostWarrantyExpense(Guid.NewGuid(), "document/journal", "FIC-JOURNAL-300",
            [new("warranty", 25, 0, "GBP"), new("reserve", 0, 25, "GBP")], "2026-09", false);

        var mapped = Cx02JournalMapper.Map(command, Settings);

        Assert.Equal("FIC-JOURNAL-300", mapped.Header["externalDocumentNumber"]);
        Assert.Equal("FIC-6100", mapped.Lines[0]["accountNumber"]);
        Assert.Equal("FIC-2100", mapped.Lines[1]["accountNumber"]);
    }

    [Fact]
    public void Replacement_mapper_uses_the_extension_sales_shipment_contract()
    {
        var command = new CreateReplacementDelivery(Guid.NewGuid(), "document/replacement", "FIC-REPLACE-300",
            "customer/300", "FIC-SHIP", new(Unit, "FIC-SERIAL-300"), OpenEnum.Known("zero"),
            "FIC-INVOICE-300", "warranty");

        var mapped = Cx02TrackedDocumentMapper.Replacement(command, "FIC-CUSTOMER-300", RemoteUnit, Settings);

        Assert.Equal("salesShipment", mapped.DocumentType);
        Assert.Equal("WH-A", Assert.Single(mapped.Lines).LocationCode);
        Assert.Equal("FIC-SHIP", mapped.Metadata["shipToCode"]);
    }

    [Fact]
    public void Stock_mapper_resolves_both_tenant_location_mappings()
    {
        var command = new ChangeStockStatusOrLocation(Guid.NewGuid(), "document/stock", "FIC-STOCK-300",
            new(Unit, "FIC-SERIAL-300"), "store", "repair", "inspection");

        var mapped = Cx02TrackedDocumentMapper.Stock(command, RemoteUnit, Settings);

        Assert.Equal("transferShipment", mapped.DocumentType);
        Assert.Equal("WH-A", mapped.Metadata["fromLocationCode"]);
        Assert.Equal("WH-R", mapped.Metadata["toLocationCode"]);
    }

    [Fact]
    public void Stock_adjustment_without_a_source_uses_the_item_journal_contract()
    {
        var command = new ChangeStockStatusOrLocation(Guid.NewGuid(), "document/stock", "FIC-STOCK-301",
            new(Unit, "FIC-SERIAL-300"), null, "repair", "inspection");

        var mapped = Cx02TrackedDocumentMapper.Stock(command, RemoteUnit, Settings);

        Assert.Equal("itemJournal", mapped.DocumentType);
        Assert.Equal("WH-R", mapped.Metadata["locationCode"]);
        Assert.Equal("inspection", mapped.Metadata["reasonCode"]);
    }

    [Fact]
    public void Service_mapper_carries_the_unit_identity_in_action_metadata()
    {
        var command = new CreateServiceOrder(Guid.NewGuid(), "document/service", "FIC-SERVICE-300",
            "customer/300", new(Unit, "FIC-SERIAL-300"), "covered", "Replace server power supply");

        var mapped = Cx02TrackedDocumentMapper.Service(command, "FIC-CUSTOMER-300", RemoteUnit);

        Assert.Equal("serviceOrder", mapped.DocumentType);
        Assert.Equal("HW-SERVER-300", mapped.Metadata["itemNumber"]);
        Assert.Equal("FIC-SERIAL-300", mapped.Metadata["serialNumber"]);
    }

    [Fact]
    public void Attachment_mapper_accepts_documented_parents_and_rejects_unknown_parents()
    {
        var parent = Guid.NewGuid();

        var target = Cx02AttachmentMapper.Target($"salesInvoices:{parent}");

        Assert.Equal("documentAttachments", target.Resource);
        Assert.Equal(parent, target.ParentId);
        Assert.EndsWith(".pdf", Cx02AttachmentMapper.FileName("FIC-ATTACH-300", "service-note.pdf", Settings));
        Assert.Throws<Cx02OutboundRuleException>(() => Cx02AttachmentMapper.Target($"unsupported:{parent}"));
    }
}
