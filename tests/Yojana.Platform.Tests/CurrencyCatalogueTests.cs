using Xunit;
using Yojana.Platform.Contracts.Tenancy;
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

    [Fact]
    public async Task Working_currency_is_set_once_and_accepted_currencies_stay_editable()
    {
        var currency = new TenantCurrency(new MemorySettings(), Catalogue);
        Assert.Equal("EUR", await currency.SetAsync("eur", CancellationToken.None));
        Assert.Equal("EUR", await currency.SetAsync("EUR", CancellationToken.None));
        var locked = await Assert.ThrowsAsync<TenantCurrencyLockedException>(() => currency.SetAsync("USD", CancellationToken.None));
        Assert.Equal("EUR", locked.Current);
        Assert.Equal("EUR", await currency.GetAsync(CancellationToken.None));

        Assert.Equal(["EUR", "GBP", "USD"], await currency.SetAcceptedAsync(["USD", "GBP"], CancellationToken.None));
        Assert.Equal(["EUR", "GBP"], await currency.SetAcceptedAsync(["GBP"], CancellationToken.None));
    }

    private sealed class MemorySettings : ITenantSettings
    {
        private readonly Dictionary<string, string> values = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            values[key] = value;
            return Task.CompletedTask;
        }
    }
}
