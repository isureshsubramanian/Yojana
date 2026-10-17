using Xunit;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Tests;

public sealed class CurrencyCatalogueTests
{
    private static readonly RuntimeCurrencyCatalogue Catalogue = new();

    [Theory]
    [InlineData("EUR", 2)]
    [InlineData("GBP", 2)]
    [InlineData("INR", 2)]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    public void Common_currencies_are_listed_with_their_minor_digits(string code, int digits)
    {
        var info = Catalogue.Find(code.ToLowerInvariant());
        Assert.NotNull(info);
        Assert.Equal((code, digits), (info.Code, info.MinorDigits));
        Assert.False(string.IsNullOrWhiteSpace(info.Name));
    }

    [Fact]
    public void Codes_are_unique_sorted_and_unknown_codes_are_refused()
    {
        Assert.Equal(Catalogue.All.Select(c => c.Code).Order(StringComparer.Ordinal), Catalogue.All.Select(c => c.Code));
        Assert.Equal(Catalogue.All.Count, Catalogue.All.Select(c => c.Code).Distinct().Count());
        Assert.Null(Catalogue.Find("ZZZ"));
        Assert.Null(Catalogue.Find("EU"));
    }
}
