using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX01.Testing;

public sealed class Cx01CompletenessTests
{
    [Theory]
    [InlineData("delivery")]
    [InlineData("credit")]
    [InlineData("journal")]
    [InlineData("transfer")]
    [InlineData("attachment")]
    [InlineData("service")]
    [InlineData("return")]
    public void Complete_documents_pass_the_source_gate(string scenario) =>
        Assert.Empty(Cx01OutboundCompleteness.RequiredFields(Cx01DocumentScenarios.Command(scenario, "complete"), Cx01DocumentScenarios.Options));

    public static IEnumerable<object[]> MissingFields()
    {
        yield return ["delivery", "customer_ref", ""];
        yield return ["delivery", "ship_to_ref", ""];
        yield return ["delivery", "ship_to_ref", "default"];
        yield return ["delivery", "replacement_unit.identifier_value", ""];
        yield return ["delivery", "replacement_unit.unit_id", Guid.Empty.ToString()];
        yield return ["credit", "customer_ref", ""];
        yield return ["credit", "lines[0].amount.currency", ""];
        yield return ["credit", "lines[0].amount.currency", "XXX"];
        yield return ["credit", "lines[0].amount.amount", "0"];
        yield return ["credit", "lines[0].quantity", "0"];
        yield return ["journal", "journal_lines[0].currency", ""];
        yield return ["journal", "journal_lines[0].currency", "XXX"];
        yield return ["journal", "journal_lines[0].account_ref", ""];
        yield return ["journal", "journal_lines[0].amount", "0"];
        yield return ["transfer", "unit.identifier_value", ""];
        yield return ["transfer", "unit.unit_id", Guid.Empty.ToString()];
        yield return ["transfer", "from_location_or_status", ""];
        yield return ["transfer", "to_location_or_status", ""];
        yield return ["service", "customer_ref", ""];
        yield return ["service", "unit.identifier_value", ""];
        yield return ["service", "unit.unit_id", Guid.Empty.ToString()];
        yield return ["service", "problem_description", ""];
        yield return ["attachment", "target_record_ref", ""];
        yield return ["attachment", "content_ref", ""];
        yield return ["attachment", "display_name", "certificate"];
    }

    [Theory]
    [MemberData(nameof(MissingFields))]
    public void Missing_source_names_the_field(string scenario, string field, string value)
    {
        var command = Cx01DocumentScenarios.Command(scenario, "missing");
        var json = JsonSerializer.SerializeToNode(command, command.GetType(), IntegrationContracts.Json)!;
        var path = field == "journal_lines[0].amount" ? "journal_lines[0].debit" : field;
        JsonNode node = json;
        var parts = path.Split('.');
        foreach (var part in parts[..^1])
        {
            var pieces = part.Split('[');
            node = node[pieces[0]]!;
            if (pieces.Length > 1) node = node[int.Parse(pieces[1].TrimEnd(']'), System.Globalization.CultureInfo.InvariantCulture)]!;
        }
        node[parts[^1]] = parts[^1] is "amount" or "quantity" or "debit" ? JsonValue.Create(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(value);
        var changed = OutboundCompleteness.Read(command.GetType().Name, json.ToJsonString());
        Assert.Contains(Cx01OutboundCompleteness.RequiredFields(changed, Cx01DocumentScenarios.Options), x => x.StartsWith(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("delivery")]
    [InlineData("transfer")]
    [InlineData("service")]
    public void Unit_id_is_never_accepted_as_its_serial(string scenario)
    {
        var command = Cx01DocumentScenarios.Command(scenario, "guid-serial");
        var placeholder = new UnitReference(Cx01DocumentScenarios.UnitId, Cx01DocumentScenarios.UnitId.ToString("N"));
        command = command switch
        {
            CreateReplacementDelivery value => value with { ReplacementUnit = placeholder },
            ChangeStockStatusOrLocation value => value with { Unit = placeholder },
            CreateServiceOrder value => value with { Unit = placeholder }, _ => command,
        };
        Assert.Contains(Cx01OutboundCompleteness.RequiredFields(command, Cx01DocumentScenarios.Options), x => x.EndsWith("identifier_value", StringComparison.Ordinal));
    }

    [Fact]
    public void Attachment_content_needs_bytes_and_its_actual_content_type()
    {
        Assert.Contains("content_ref", Cx01OutboundCompleteness.ContentFields(null));
        Assert.Contains("content.bytes", Cx01OutboundCompleteness.ContentFields(new([], "application/pdf")));
        Assert.Contains("content.content_type", Cx01OutboundCompleteness.ContentFields(new([1], "")));
        Assert.Empty(Cx01OutboundCompleteness.ContentFields(new([1], "application/pdf")));
    }

    [Fact]
    public void Missing_structures_and_price_are_named()
    {
        var credit = (CreateCreditMemo)Cx01DocumentScenarios.Command("credit", "lines");
        Assert.Contains("lines", Cx01OutboundCompleteness.RequiredFields(credit with { Lines = [] }, Cx01DocumentScenarios.Options));
        Assert.Contains("lines[0].item_external_id", Cx01OutboundCompleteness.RequiredFields(credit with { Lines = [credit.Lines[0] with { UnitId = null }] }, Cx01DocumentScenarios.Options));
        var delivery = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "price");
        var priced = delivery with { PricePolicy = OpenEnum.Known("full"), Price = new(25, "") };
        Assert.Contains("price.currency", Cx01OutboundCompleteness.RequiredFields(priced, Cx01DocumentScenarios.Options));
        Assert.Contains("price_policy", Cx01OutboundCompleteness.RequiredFields(delivery with { PricePolicy = OpenEnum.Known("unknown") }, Cx01DocumentScenarios.Options));
    }

    [Fact]
    public void Foreign_journals_wait_for_local_amounts_instead_of_inventing_an_exchange_rate()
    {
        var journal = (PostWarrantyExpense)Cx01DocumentScenarios.Command("journal", "foreign");
        var foreign = journal with { JournalLines = journal.JournalLines.Select(x => x with { Currency = "EUR" }).ToList() };
        Assert.Contains("journal_lines[0].local_amounts", Cx01OutboundCompleteness.RequiredFields(foreign, Cx01DocumentScenarios.Options));
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("journal")]
    [InlineData("delivery")]
    public void Unmapped_currency_and_missing_local_currency_are_held(string scenario)
    {
        IOutboundCommand command = Cx01DocumentScenarios.Command(scenario, "currency-map");
        if (command is CreateReplacementDelivery delivery)
            command = delivery with { PricePolicy = OpenEnum.Known("full"), Price = new(25, "USD") };
        Assert.Contains(Cx01OutboundCompleteness.RequiredFields(command, Cx01DocumentScenarios.Options with { CurrencyMap = null }),
            x => x.Contains("currency not mapped", StringComparison.Ordinal));
        Assert.Contains("settings.local_currency_code", Cx01OutboundCompleteness.RequiredFields(command, Cx01DocumentScenarios.Options with { LocalCurrencyCode = null }));
    }

    [Fact]
    public void Empty_and_unbalanced_journals_and_unpriced_deliveries_are_held()
    {
        var journal = (PostWarrantyExpense)Cx01DocumentScenarios.Command("journal", "journal");
        Assert.Contains("journal_lines", Cx01OutboundCompleteness.RequiredFields(journal with { JournalLines = [] }, Cx01DocumentScenarios.Options));
        Assert.Contains("journal_lines.balancing_account", Cx01OutboundCompleteness.RequiredFields(journal with { JournalLines = [journal.JournalLines[0]] }, Cx01DocumentScenarios.Options));
        var delivery = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "price");
        Assert.Contains("price.amount", Cx01OutboundCompleteness.RequiredFields(delivery with { PricePolicy = OpenEnum.Known("full") }, Cx01DocumentScenarios.Options));
    }
}
