using Yojana.DemoIncumbent;
using Xunit;

namespace Yojana.DemoIncumbent.Tests;

public sealed class ScenarioCatalogueTests
{
    [Fact]
    public void Every_service_ticket_is_present_once()
    {
        var expected = Enumerable.Range(1, 74).Select(value => "T" + value).ToHashSet(StringComparer.Ordinal);
        expected.Remove("T66");
        expected.Add("T66a");
        expected.Add("T66b");

        Assert.Equal(expected.Order(), ScenarioCatalogue.TicketIds.Order());
        Assert.All(ScenarioCatalogue.TicketIds.GroupBy(value => value), group => Assert.Single(group));
    }

    [Fact]
    public void Every_scenario_is_data_backed_or_explicitly_product_only()
    {
        Assert.All(ScenarioCatalogue.All, scenario =>
        {
            if (scenario.Disposition == DemoDisposition.ProductOnly)
                Assert.Empty(scenario.RecipeKeys);
            else
                Assert.NotEmpty(scenario.RecipeKeys);
        });
    }

    [Fact]
    public void Marker_owners_are_unique_and_bounded()
    {
        var markers = ScenarioCatalogue.All.Select(scenario => scenario.MarkerTicket).ToArray();
        Assert.Equal(markers.Length, markers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(markers, marker => Assert.Matches("^T(?:[1-9]|[1-6][0-9]|7[0-4]|66[ab])$", marker));
    }

    [Fact]
    public void Data_scenarios_produce_a_few_hundred_marked_serials_without_unintended_collisions()
    {
        var serials = ScenarioCatalogue.All.Where(scenario => scenario.Disposition == DemoDisposition.Data)
            .SelectMany(ScenarioCatalogue.SerialsFor).ToArray();
        var groups = serials.GroupBy(serial => serial.Trim(), StringComparer.OrdinalIgnoreCase);
        var intentionalCrossItemCollision = Assert.Single(groups, group => group.Count() > 1);

        Assert.Equal("YD-T2-001", intentionalCrossItemCollision.Key);
        Assert.InRange(serials.Length, 250, 500);
        Assert.All(serials, serial => Assert.StartsWith("YD-", serial.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalogue_does_not_name_forbidden_products_or_real_companies()
    {
        var text = string.Join('\n', ScenarioCatalogue.All.Select(scenario => scenario.ToString()));
        foreach (var forbidden in new[] { "Devon" + "think", "S" + "AP", "Business" + " One", "Dell", "HP", "Lenovo", "Cisco" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
}
