using System.Security.Cryptography;
using System.Text;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX02;

internal sealed record Cx02MappedLine(
    Dictionary<string, object?> Body,
    string ItemNumber,
    string SerialNumber,
    decimal Quantity,
    string? LocationCode = null);

internal static class Cx02CreditMapper
{
    public static (Dictionary<string, object?> Header, IReadOnlyList<Cx02MappedLine> Lines) Map(
        CreateCreditMemo command, string customer, IReadOnlyList<Cx02OutboundUnit?> units,
        Cx02OutboundSettings settings)
    {
        if (command.Lines.Count == 0) throw new Cx02OutboundRuleException("The credit memo has no lines.");
        if (command.RestockingFee is { Amount: not 0 })
            throw new Cx02OutboundRuleException("A restocking fee needs a configured charge item.");
        if (command.Lines.Select(line => line.Amount.Currency).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new Cx02OutboundRuleException("A credit memo must use one currency.");
        var lines = command.Lines.Select((line, index) =>
        {
            if (line.Quantity <= 0 || line.Amount.Amount <= 0)
                throw new Cx02OutboundRuleException("Credit memo quantities and amounts must be positive.");
            var unit = units[index];
            var item = unit?.ItemNumber ?? line.ItemExternalId
                ?? throw new Cx02OutboundRuleException("Map every credit memo line to an item or unit.");
            var serial = unit?.SerialNumber ?? line.Serials?.SingleOrDefault()?.Value ?? "";
            if (line.Serials is { Count: > 1 })
                throw new Cx02OutboundRuleException("Each tracked credit line must identify one serial-managed unit.");
            return new Cx02MappedLine(new()
            {
                ["sequence"] = (index + 1) * 10,
                ["lineType"] = "Item",
                ["lineObjectNumber"] = item,
                ["description"] = line.Description,
                ["quantity"] = line.Quantity,
                ["unitPrice"] = line.Amount.Amount / line.Quantity,
            }, item, serial, line.Quantity, unit?.LocationCode);
        }).ToList();
        return (new()
        {
            ["customerNumber"] = customer,
            ["externalDocumentNumber"] = command.ErpReferenceText,
            ["currencyCode"] = command.Lines[0].Amount.Currency,
        }, lines);
    }
}

internal static class Cx02JournalMapper
{
    public static (Dictionary<string, object?> Header, IReadOnlyList<Dictionary<string, object?>> Lines) Map(
        PostWarrantyExpense command, Cx02OutboundSettings settings)
    {
        if (command.AccrualSuppressed || command.JournalLines.Count == 0)
            throw new Cx02OutboundRuleException("The warranty expense has no journal lines.");
        if (command.JournalLines.Sum(line => line.Debit - line.Credit) != 0
            || command.JournalLines.Select(line => line.Currency).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new Cx02OutboundRuleException("The journal must balance in one currency.");
        var lines = command.JournalLines.Select((line, index) =>
        {
            if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0) == (line.Credit > 0))
                throw new Cx02OutboundRuleException("Journal lines need non-negative, one-sided amounts.");
            var account = settings.AccountMap?.GetValueOrDefault(line.AccountRef)
                ?? throw new Cx02OutboundRuleException($"Map the warranty account '{line.AccountRef}' before posting.");
            return new Dictionary<string, object?>
            {
                ["lineNumber"] = (index + 1) * 10,
                ["accountNumber"] = account,
                ["debitAmount"] = line.Debit,
                ["creditAmount"] = line.Credit,
                ["currencyCode"] = line.Currency,
            };
        }).ToList();
        return (new()
        {
            ["externalDocumentNumber"] = command.ErpReferenceText,
            ["description"] = command.CohortTag ?? "Warranty expense",
        }, lines);
    }
}

internal static class Cx02TrackedDocumentMapper
{
    public static (string DocumentType, IReadOnlyList<Cx02MappedLine> Lines, Dictionary<string, object?> Metadata)
        Replacement(CreateReplacementDelivery command, string customer, Cx02OutboundUnit unit,
            Cx02OutboundSettings settings)
    {
        if (command.PricePolicy.Value is not ("zero" or "difference" or "full"))
            throw new Cx02OutboundRuleException("The replacement price policy is not supported.");
        if (string.IsNullOrWhiteSpace(command.ShipToRef) || command.ShipToRef == "default")
            throw new Cx02OutboundRuleException("The replacement needs a ship-to reference.");
        var price = command.PricePolicy.Value == "zero" ? 0m
            : command.Price?.Amount ?? throw new Cx02OutboundRuleException("The replacement price is missing.");
        return ("salesShipment", [new(new()
        {
            ["itemNumber"] = unit.ItemNumber, ["quantity"] = 1m, ["unitPrice"] = price,
        }, unit.ItemNumber, unit.SerialNumber, 1m, MapLocation(unit.LocationCode, settings))], new()
        {
            ["customerNumber"] = customer, ["shipToCode"] = command.ShipToRef,
            ["reasonCode"] = command.ReasonCodeText, ["originalInvoiceReference"] = command.OriginalInvoiceRef,
        });
    }

    public static (string DocumentType, IReadOnlyList<Cx02MappedLine> Lines, Dictionary<string, object?> Metadata)
        Stock(ChangeStockStatusOrLocation command, Cx02OutboundUnit unit, Cx02OutboundSettings settings)
    {
        var from = MapLocation(command.FromLocationOrStatus, settings);
        var to = MapLocation(command.ToLocationOrStatus, settings);
        if (string.IsNullOrWhiteSpace(to))
            throw new Cx02OutboundRuleException("A stock change needs a mapped destination location.");
        if (string.IsNullOrWhiteSpace(from))
            return ("itemJournal", [new(new() { ["itemNumber"] = unit.ItemNumber, ["quantity"] = 1m },
                unit.ItemNumber, unit.SerialNumber, 1m, to)], new()
            {
                ["locationCode"] = to, ["reasonCode"] = command.Reason,
            });
        if (from == to)
            throw new Cx02OutboundRuleException("A stock move needs distinct mapped source and destination locations.");
        return ("transferShipment", [new(new() { ["itemNumber"] = unit.ItemNumber, ["quantity"] = 1m },
            unit.ItemNumber, unit.SerialNumber, 1m, from)], new()
        {
            ["fromLocationCode"] = from, ["toLocationCode"] = to, ["reason"] = command.Reason,
        });
    }

    public static (string DocumentType, IReadOnlyList<Cx02MappedLine> Lines, Dictionary<string, object?> Metadata)
        Service(CreateServiceOrder command, string customer, Cx02OutboundUnit unit) =>
        ("serviceOrder", [new(new() { ["itemNumber"] = unit.ItemNumber, ["quantity"] = 1m },
            unit.ItemNumber, unit.SerialNumber, 1m, unit.LocationCode)], new()
        {
            ["customerNumber"] = customer, ["coverageDecision"] = command.CoverageDecisionText,
            ["description"] = command.ProblemDescription,
            ["itemNumber"] = unit.ItemNumber,
            ["serialNumber"] = unit.SerialNumber,
        });

    private static string? MapLocation(string? source, Cx02OutboundSettings settings) =>
        string.IsNullOrWhiteSpace(source) ? null : settings.LocationMap?.GetValueOrDefault(source) ?? source;
}

internal static class Cx02AttachmentMapper
{
    private static readonly Dictionary<string, (string Resource, string ParentType)> Parents =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["salesCreditMemos"] = ("documentAttachments", "Sales Credit Memo"),
            ["salesOrders"] = ("documentAttachments", "Sales Order"),
            ["salesQuotes"] = ("attachments", "Sales Quote"),
            ["salesInvoices"] = ("documentAttachments", "Sales Invoice"),
            ["purchaseInvoices"] = ("documentAttachments", "Purchase Invoice"),
            ["purchaseOrders"] = ("documentAttachments", "Purchase Order"),
            ["purchaseCreditMemos"] = ("documentAttachments", "Purchase Credit Memo"),
            ["items"] = ("documentAttachments", "Item"),
            ["customers"] = ("documentAttachments", "Customer"),
            ["vendors"] = ("documentAttachments", "Vendor"),
            ["employees"] = ("documentAttachments", "Employee"),
            ["projects"] = ("documentAttachments", "Project"),
            ["journals"] = ("attachments", "Journal"),
            ["generalLedgerEntries"] = ("attachments", "Journal"),
        };

    public static (string Resource, string ParentType, Guid ParentId) Target(string reference)
    {
        var parts = reference.Split(':', 2);
        if (parts.Length != 2 || !Parents.TryGetValue(parts[0], out var parent)
            || !Guid.TryParse(parts[1], out var id))
            throw new Cx02OutboundRuleException("The attachment target is not a supported mapped parent.");
        return (parent.Resource, parent.ParentType, id);
    }

    public static string FileName(string logicalReference, string displayName, Cx02OutboundSettings settings)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0 || !settings.AttachmentExtensions.Contains(extension))
            throw new Cx02OutboundRuleException("The attachment file type is not allowed on this connection.");
        return "yj-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logicalReference)))
            .ToLowerInvariant() + "." + extension;
    }

    public static string? ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".txt" => "text/plain",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", _ => null,
    };
}
