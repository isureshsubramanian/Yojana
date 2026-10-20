using Microsoft.Extensions.Configuration;
using Xunit;
using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX01.Testing;

public sealed class Cx01DocumentFixTests
{
    [Theory]
    [InlineData("USD", null)]
    [InlineData("EUR", "EUR")]
    public void Currency_mapping_omits_local_code_and_maps_foreign_code(string iso, string? wire)
    {
        var command = (CreateCreditMemo)Cx01DocumentScenarios.Command("credit", "ref");
        var body = Cx01CreditMapper.Map(command with { Lines = [command.Lines[0] with { Amount = new(1.235m, iso) }] },
            "C", [Cx01DocumentScenarios.Unit], new("01"), null, Cx01DocumentScenarios.Options);
        var line = Assert.Single((Dictionary<string, object?>[])body["DocumentLines"]!);
        Assert.Equal(1.24m, line["LineTotal"]);
        Assert.Equal(wire, line.GetValueOrDefault("Currency"));
    }

    [Theory]
    [InlineData("XXX")]
    [InlineData("GBP")]
    public void Missing_or_unmapped_currency_is_unsupported(string currency) =>
        Assert.Throws<Cx01UnmappableException>(() => Cx01Money.Code(currency, Cx01DocumentScenarios.Options));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Credit_amounts_and_quantities_must_be_positive(int amount, int quantity)
    {
        var command = (CreateCreditMemo)Cx01DocumentScenarios.Command("credit", "ref");
        Assert.Throws<Cx01DocumentRuleException>(() => Cx01CreditMapper.Map(command with
        { Lines = [command.Lines[0] with { Amount = new(amount, "USD"), Quantity = quantity, ItemExternalId = "I1" }] }, "C",
            [null], new("01"), null, Cx01DocumentScenarios.Options));
    }

    [Fact]
    public void Charge_policy_cannot_be_overridden_by_zero_price_setting()
    {
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "ref");
        Assert.Throws<Cx01UnmappableException>(() => Cx01DeliveryMapper.Map(command with { PricePolicy = OpenEnum.Known("full") },
            "C", Cx01DocumentScenarios.Unit, new("01"), Cx01DocumentScenarios.Options, null));
    }

    [Fact]
    public void Credit_cannot_mix_currencies_and_journal_cannot_contain_empty_lines()
    {
        var command = (CreateCreditMemo)Cx01DocumentScenarios.Command("credit", "ref");
        Assert.Throws<Cx01DocumentRuleException>(() => Cx01CreditMapper.Map(command with
        { Lines = [command.Lines[0], command.Lines[0] with { Amount = new(1, "EUR") }] }, "C", [null, null], new("01"), null, Cx01DocumentScenarios.Options));
        var journal = (PostWarrantyExpense)Cx01DocumentScenarios.Command("journal", "ref");
        Assert.Throws<Cx01DocumentRuleException>(() => Cx01JournalMapper.Map(journal with
        { JournalLines = [.. journal.JournalLines, new("warranty", 0, 0, "USD")] }, Cx01DocumentScenarios.Options));
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("Resolution")]
    public void Memo_reference_fields_are_rejected_at_settings_save(string field)
    {
        var settings = new Cx01ConnectionSettings(new("https://fictional.example.test"), "FIC", "test", FieldMap:
            new Dictionary<string, Cx01FieldSource> { ["outbound.service.reference"] = new(field) });
        Assert.Contains("cannot be filtered", Assert.Throws<Cx01DocumentRuleException>(() => settings.Validate()).Message);
        Assert.Equal("Subject", Cx01DocumentCatalogue.ReferenceField("ServiceCalls", settings with { FieldMap = null }));
    }

    [Fact]
    public async Task Long_reference_is_rejected_before_posting_when_metadata_knows_the_length()
    {
        var settings = new Cx01ConnectionSettings(new("https://fictional.example.test"), "FIC", "test");
        await Assert.ThrowsAsync<Cx01DocumentRuleException>(() => Cx01ReferenceRules.CheckAsync(new ShortFieldSurvey(), Guid.NewGuid(),
            "JournalEntries", new string('r', 57), settings, default));
    }

    [Fact]
    public void Pending_pair_key_fits_with_a_sixty_four_character_field()
    {
        var field = "U_" + new string('a', 62);
        var key = "pair:" + Cx01ReviewKey.Format(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "InventoryGenEntries", field) + "|qualified";
        Assert.True(key.Length <= 100);
        Assert.Equal(field, Cx01ReviewKey.Parse(key[5..]).Field);
    }

    [Theory]
    [InlineData("reference_only", false)]
    [InlineData("documents", false)]
    [InlineData("documents", true)]
    public void Relay_document_writes_require_mode_and_acceptance(string mode, bool accepted)
    {
        var settings = new Cx01ConnectionSettings(new("https://fictional.example.test"), "FIC", "test",
            DocumentsAcceptance: accepted ? new("actor", new(2026, 9, 1), "accepted") : null);
        var allow = new Yojana.Relay.RelayAllowList(Cx01RelayProfile.Declaration(settings, mode));
        Assert.Equal(mode == "documents" && accepted, allow.Allows("POST", Cx01Protocol.Root + "CreditNotes", ""));
        Assert.Equal(mode == "documents" && accepted, allow.Allows("POST", Cx01Protocol.Root + Cx01Protocol.Drafts, ""));
        Assert.Equal(mode == "documents" && accepted, allow.Allows("PATCH", Cx01Protocol.Root + "DeliveryNotes(1)", ""));
    }

    [Fact]
    public async Task Local_content_stops_at_the_root_rejects_child_symlinks_and_derives_content_type()
    {
        var parent = Path.Combine(Path.GetFullPath(".scratch/p8t19"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, "root");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "certificate.pdf"), [1, 2]);
        var alias = parent + "-alias";
        Directory.CreateSymbolicLink(alias, parent);
        var store = new Cx01LocalAttachmentContent(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Integration:AttachmentRoot"] = Path.Combine(alias, "root") }).Build());
        try
        {
            Assert.Equal("application/pdf", (await store.ReadAsync("certificate.pdf", 10, default))!.ContentType);
            Assert.Null(await store.ReadAsync("../certificate.pdf", 10, default));
            File.CreateSymbolicLink(Path.Combine(root, "link.pdf"), Path.Combine(root, "certificate.pdf"));
            Assert.Null(await store.ReadAsync("link.pdf", 10, default));
        }
        finally { Directory.Delete(alias); Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task Simulator_filters_the_named_field_instead_of_any_matching_value()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        await using var context = await Yojana.Integration.Tests.Cx01TestContext.CreateAsync(simulator);
        using var created = await context.Client.SendJsonAsync(context.ConnectionId, HttpMethod.Post,
            Cx01Protocol.Root + "ServiceCalls", new Dictionary<string, object?> { ["Subject"] = "subject", ["Description"] = "memo" }, false, default);
        using var wrong = await context.Client.GetAsync(context.ConnectionId, Cx01Protocol.Root + "ServiceCalls?$filter="
            + Cx01ReviewKey.FilterEquals("Subject", "memo"), default);
        Assert.Empty(wrong.RootElement.GetProperty("value").EnumerateArray());
        using var correct = await context.Client.GetAsync(context.ConnectionId, Cx01Protocol.Root + "ServiceCalls?$filter="
            + Cx01ReviewKey.FilterEquals("Subject", "subject"), default);
        Assert.Single(correct.RootElement.GetProperty("value").EnumerateArray());
    }

    [Fact]
    public void Live_probe_builds_local_currency_credit_and_serial_delivery_through_the_mappers()
    {
        var input = new Cx01SandboxDocumentScenario("C1", "Ship to", "I1", new Dictionary<string, string>
            { ["DeliveryNotes"] = "S1", ["CreditNotes"] = "S2" }, "01", "02", "6100", "2100", "USD", "$", "2026-09-01");
        var delivery = Cx01DocumentLiveTests.Map("DeliveryNotes", input);
        Assert.Equal("Ship to", delivery["ShipToCode"]);
        var credit = Cx01DocumentLiveTests.Map("CreditNotes", input);
        var line = Assert.Single((Dictionary<string, object?>[])credit["DocumentLines"]!);
        Assert.False(line.ContainsKey("Currency"));
        Assert.Equal(input.Amount, line["LineTotal"]);
    }

    private sealed class ShortFieldSurvey : ICx01SurveyReader
    {
        public Task<IReadOnlyList<string>> MandatoryWithoutDefaultFieldsAsync(Guid id, string table, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<int?> FieldLengthAsync(Guid id, string collection, string field, CancellationToken ct) => Task.FromResult<int?>(50);
    }
}
