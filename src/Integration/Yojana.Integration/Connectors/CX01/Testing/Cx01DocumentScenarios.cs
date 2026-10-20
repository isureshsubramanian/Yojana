using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01.Testing;

internal static class Cx01DocumentScenarios
{
    public static readonly Guid UnitId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static Cx01OutboundUnit Unit { get; } = new("FIC-ITEM", "FIC-SERIAL", "17");
    public static Cx01DocumentSettings Options { get; } = new(LocalCurrencyCode: "$", CurrencyMap: new Dictionary<string, string> { ["USD"] = "$", ["EUR"] = "EUR" }, AccountMap: new Dictionary<string, string>
    {
        ["warranty"] = "6100", ["reserve"] = "2100",
    }, LocationMap: new Dictionary<string, Cx01Warehouse>
    {
        ["default"] = new("01"), ["store"] = new("01"), ["repair"] = new("02"), ["hold"] = new("01"),
    });
    public static string AttachmentTarget => "Returns:5101";
    public static string AlternativeAttachmentTarget => "DeliveryNotes:9001";
    public static IReadOnlyList<string> Cases { get; } = ["delivery", "credit", "journal", "transfer", "attachment", "service", "return"];
    public static Cx01ConnectionSettings Annotate(Cx01ConnectionSettings current) => current with
        { RmaReferenceField = "U_YojanaRma", Documents = current.Documents! with { ServiceOrderAction = "annotate" } };
    public static string PostedDate(Cx01Simulator simulator, string kind) => simulator.DocumentBodies[Collection(kind)]
        .GetProperty(kind == "journal" ? "ReferenceDate" : "DocDate").GetString()!;
    public static int AnnotationPatches(Cx01Simulator simulator) => simulator.OutboundPaths.Count(x => x.Method == "PATCH" && x.Path == Cx01Protocol.Root + "Returns(5101)");
    public static void EndReview(Cx01Simulator simulator, bool rejected) => simulator.SetDraftAuthorization(7001, rejected ? "dasRejected" : "dasCancelled");
    public static string Collection(string scenario) => scenario switch
    {
        "delivery" => "DeliveryNotes", "credit" => "CreditNotes", "journal" => "JournalEntries", "transfer" => "StockTransfers",
        "attachment" => "Attachments2", "service" => "ServiceCalls", "return" => "Returns", "issue" => "InventoryGenExits", "receipt" => "InventoryGenEntries",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
    public static bool AttachmentIsLinked(Cx01Simulator simulator, string externalId) =>
        simulator.Document("Returns", 5101)?.GetValueOrDefault("AttachmentEntry")?.ToString() == externalId.Split(':').Last();

    public static IOutboundCommand Command(string scenario, string reference, Guid? document = null)
    {
        var key = Guid.CreateVersion7();
        var module = "document/" + (document ?? Guid.CreateVersion7()).ToString("N");
        return scenario switch
        {
            "delivery" => new CreateReplacementDelivery(key, module, reference, "FIC-CUSTOMER", "SHIP", new(UnitId, Unit.Serial), OpenEnum.Known("zero"), "13", "warranty"),
            "credit" => new CreateCreditMemo(key, module, reference, "FIC-CUSTOMER", [new(UnitId, "Fictional credit", 1, new(25, "USD"), BaseLine: 0)], null, "13"),
            "journal" => new PostWarrantyExpense(key, module, reference, [new("warranty", 25, 0, "USD"), new("reserve", 0, 25, "USD")], null, false),
            "transfer" => new ChangeStockStatusOrLocation(key, module, reference, new(UnitId, Unit.Serial), "store", "repair", "repair"),
            "status" => new ChangeStockStatusOrLocation(key, module, reference, new(UnitId, Unit.Serial), "store", "hold", "hold"),
            "attachment" => new AttachDocument(key, module, reference, "Returns:5101", "Fictional certificate.pdf", "fixture", "https://fictional.example.test/certificate"),
            _ => new CreateServiceOrder(key, module, reference, "FIC-CUSTOMER", new(UnitId, Unit.Serial), "covered", "Fictional repair"),
        };
    }
}

internal sealed class Cx01FixtureFacts(Yojana.Integration.Data.IntegrationDbContext? db = null, Yojana.Platform.Contracts.Tenancy.ITenantContext? tenant = null) : ICx01OutboundFacts
{
    public Task<Cx01OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, CancellationToken ct, string? suppliedSerial = null) => Task.FromResult<Cx01OutboundUnit?>(Cx01DocumentScenarios.Unit with { Serial = suppliedSerial ?? Cx01DocumentScenarios.Unit.Serial });
    public async Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference, CancellationToken ct)
    {
        if (db is not null && tenant is not null && Guid.TryParse(reference, out var id))
            return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                db.ExternalIdMaps.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
                    && x.EntityType == kind && x.ModuleId == id && x.ValidTo == null).Select(x => x.ExternalId), ct);
        return reference;
    }
}

internal sealed class Cx01FixtureContent : ICx01AttachmentContent
{
    public Task<Cx01AttachmentContent?> ReadAsync(string contentRef, long maximumBytes, CancellationToken ct) =>
        Task.FromResult<Cx01AttachmentContent?>(new(System.Text.Encoding.UTF8.GetBytes("%PDF-fictional"), "application/pdf"));
}
