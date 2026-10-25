using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class EntitlementGapReportTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\r\nb", "\"a\r\nb\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("@sum", "'@sum")]
    public void Csv_cells_are_rfc_4180_quoted_and_formula_safe(string value, string expected) =>
        Assert.Equal(expected, EntitlementGapCsv.Cell(value));

    [Fact]
    public void Filters_parse_lists_dates_drop_ship_and_sort()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var filters = EntitlementGapFilterParser.Parse($"{first}, {second},{first}", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            "delivery,invoice", "only", "exception,open", "serial");
        Assert.Equal([first, second], filters.ItemIds);
        Assert.Equal(["delivery", "invoice"], filters.Channels);
        Assert.Equal(EntitlementGapDropShip.Only, filters.DropShip);
        Assert.Equal(["exception", "open"], filters.RequirementStatuses);
        Assert.Equal("serial", filters.Sort);
    }

    [Fact]
    public void Invalid_filters_are_refused()
    {
        Assert.Throws<ArgumentException>(() => EntitlementGapFilterParser.Parse("not-a-guid", null, null, null, null, null, null));
        Assert.Throws<ArgumentException>(() => EntitlementGapFilterParser.Parse(null, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), null, null, null, null));
        Assert.Throws<ArgumentException>(() => EntitlementGapFilterParser.Parse(null, null, null, null, "maybe", null, null));
    }
}
