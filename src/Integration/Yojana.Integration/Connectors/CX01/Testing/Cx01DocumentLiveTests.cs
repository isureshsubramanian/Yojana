using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01.Testing;

internal sealed class Cx01DocumentLiveTheoryAttribute : TheoryAttribute
{
    public Cx01DocumentLiveTheoryAttribute()
    {
        if (new[] { "YOJANA_CX01_URL", "YOJANA_CX01_USER", "YOJANA_CX01_PASSWORD", "YOJANA_CX01_COMPANY", "YOJANA_CX01_DOCUMENT_SCENARIO" }
            .Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x))))
            Skip = "CX01 sandbox credentials and YOJANA_CX01_DOCUMENT_SCENARIO are required; these tests post documents.";
    }
}

/// <summary>Opt-in sandbox writes built by the production family mappers. The scenario file supplies customer,
/// ship-to name, item, per-collection serials, warehouses, accounts, ISO/local currency codes and a document date.
/// Each collection needs a serial with the correct stock state in the isolated sandbox company.</summary>
public sealed class Cx01DocumentLiveTests
{
    internal static Dictionary<string, object?> Map(string collection, Cx01SandboxDocumentScenario input)
    {
        var options = new Cx01DocumentSettings(LocalCurrencyCode: input.LocalCurrencyCode,
            CurrencyMap: new Dictionary<string, string> { [input.CurrencyIso] = input.LocalCurrencyCode },
            AccountMap: new Dictionary<string, string> { ["warranty"] = input.ExpenseAccount, ["reserve"] = input.OffsetAccount });
        var reference = "YOJANA:LIVE-" + Guid.NewGuid().ToString("N");
        var unit = new Cx01OutboundUnit(input.Item, input.Serials.GetValueOrDefault(collection, ""));
        var warehouse = new Cx01Warehouse(input.FromWarehouse);
        var delivery = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", reference);
        var credit = (CreateCreditMemo)Cx01DocumentScenarios.Command("credit", reference);
        var service = (CreateServiceOrder)Cx01DocumentScenarios.Command("service", reference);
        var journal = (PostWarrantyExpense)Cx01DocumentScenarios.Command("journal", reference);
        var body = collection switch
        {
            "DeliveryNotes" => Cx01DeliveryMapper.Map(delivery with { ShipToRef = input.ShipToName }, input.Customer, unit, warehouse, options, null),
            "CreditNotes" => Cx01CreditMapper.Map(credit with { Lines = [credit.Lines[0] with { Amount = new(input.Amount, input.CurrencyIso) }] }, input.Customer, [unit], warehouse, null, options),
            "Returns" => Cx01ReturnMapper.Map(service, input.Customer, unit, warehouse, new(new("https://fictional.example.test"), "FIC", "test")),
            "ServiceCalls" => Cx01ServiceMapper.Map(service, input.Customer, unit),
            "JournalEntries" => Cx01JournalMapper.Map(journal with { JournalLines = [new("warranty", input.Amount, 0, input.CurrencyIso), new("reserve", 0, input.Amount, input.CurrencyIso)] }, options),
            "StockTransfers" => Cx01StockMapper.Transfer(unit, warehouse, new(input.ToWarehouse)),
            "InventoryGenExits" => Cx01StockMapper.Adjustment(unit, warehouse, input.ExpenseAccount),
            "InventoryGenEntries" => Cx01StockMapper.Adjustment(unit, warehouse, input.OffsetAccount),
            _ => throw new ArgumentOutOfRangeException(nameof(collection)),
        };
        var configured = new Cx01ConnectionSettings(new("https://fictional.example.test"), "FIC", "test");
        body[Cx01DocumentCatalogue.ReferenceField(collection, configured)] = reference;
        if (collection != "ServiceCalls") body[collection == "JournalEntries" ? "ReferenceDate" : "DocDate"] = input.DocumentDate;
        return body;
    }

    [Cx01DocumentLiveTheory]
    [Trait("Category", "Cx01Live")]
    [InlineData("DeliveryNotes")]
    [InlineData("CreditNotes")]
    [InlineData("Returns")]
    [InlineData("JournalEntries")]
    [InlineData("StockTransfers")]
    [InlineData("InventoryGenEntries")]
    [InlineData("InventoryGenExits")]
    [InlineData("Attachments2")]
    [InlineData("ServiceCalls")]
    public async Task Post_one_sandbox_document_and_read_its_full_body(string collection)
    {
        using var scenario = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("YOJANA_CX01_DOCUMENT_SCENARIO")!));
        var input = scenario.RootElement.Deserialize<Cx01SandboxDocumentScenario>(IntegrationContracts.Json)!;
        var body = collection == "Attachments2" ? default : JsonSerializer.SerializeToElement(Map(collection, input), Cx01Protocol.Json);
        var baseUri = new Uri(Environment.GetEnvironmentVariable("YOJANA_CX01_URL")!);
        // A sandbox with a self-signed service certificate is trusted only by its exact SHA-256 pin, never "accept any".
        var pin = Environment.GetEnvironmentVariable("YOJANA_CX01_CERT_SHA256")?.Replace(":", "", StringComparison.Ordinal);
        var handler = new HttpClientHandler { UseCookies = false };
        if (!string.IsNullOrWhiteSpace(pin))
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || (certificate is not null && string.Equals(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData)), pin,
                    StringComparison.OrdinalIgnoreCase));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUri.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromMinutes(2),
        };
        using var login = await client.PostAsync(Cx01Protocol.Login, new StringContent(JsonSerializer.Serialize(new
        {
            CompanyDB = Environment.GetEnvironmentVariable("YOJANA_CX01_COMPANY"),
            UserName = Environment.GetEnvironmentVariable("YOJANA_CX01_USER"),
            Password = Environment.GetEnvironmentVariable("YOJANA_CX01_PASSWORD"),
        }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookies = login.Headers.GetValues("Set-Cookie").Select(x => x.Split(';', 2)[0]);
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        try
        {
            using HttpContent upload = collection == "Attachments2"
                ? new Cx01MultipartUpload(Cx01AttachmentMapper.FileName("YOJANA:LIVE-" + Guid.NewGuid().ToString("N"), Path.GetFileName(input.FilePath!), new()),
                    await File.ReadAllBytesAsync(input.FilePath!), Cx01AttachmentMapper.ContentType(Path.GetExtension(input.FilePath!))!).CreateContent()
                : new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
            using var created = await client.PostAsync(Cx01Protocol.Root + collection, upload);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            using var posted = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var key = Cx01ReviewKey.Text(posted.RootElement, Cx01DocumentCatalogue.KeyField(collection));
            Assert.NotNull(key);
            using var read = await client.GetAsync(Cx01Protocol.Root + collection + "(" + Cx01Protocol.KeyLiteral(key) + ")");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var observed = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal(key, Cx01ReviewKey.Text(observed.RootElement, Cx01DocumentCatalogue.KeyField(collection)));
            // An upload has no JSON body to compare serials against.
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("DocumentLines", out var expected))
                for (var i = 0; i < expected.GetArrayLength(); i++)
                    if (expected[i].TryGetProperty("SerialNumbers", out var serials) && serials.GetArrayLength() > 0)
                        Assert.NotEmpty(observed.RootElement.GetProperty("DocumentLines")[i].GetProperty("SerialNumbers").EnumerateArray());
        }
        finally
        {
            using var logout = await client.PostAsync(Cx01Protocol.Logout, null);
        }
    }
}

internal sealed record Cx01SandboxDocumentScenario(string Customer, string ShipToName, string Item,
    IReadOnlyDictionary<string, string> Serials, string FromWarehouse, string ToWarehouse,
    string ExpenseAccount, string OffsetAccount, string CurrencyIso, string LocalCurrencyCode,
    string DocumentDate, decimal Amount = 1, string? FilePath = null);
