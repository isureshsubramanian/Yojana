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
    public async Task Home_currency_changes_until_the_first_amount_then_reports_why_it_is_locked()
    {
        var usage = new MemoryUsage();
        var currency = new TenantCurrency(new MemorySettings(), Catalogue, [usage]);
        Assert.Equal("EUR", await currency.SetAsync("eur", CancellationToken.None));
        Assert.Equal("EUR", await currency.SetAsync("EUR", CancellationToken.None));
        Assert.Equal("USD", await currency.SetAsync("USD", CancellationToken.None));
        Assert.True((await currency.GetStatusAsync(CancellationToken.None)).Changeable);

        usage.Count = 1;
        var locked = await Assert.ThrowsAsync<TenantCurrencyLockedException>(() => currency.SetAsync("EUR", CancellationToken.None));
        Assert.Equal("USD", locked.Current);
        Assert.Equal("working_currency_locked", TenantCurrencyLockedException.Rule);
        Assert.Equal([new CurrencyUsage("claims", 1)], locked.Usage);
        Assert.False((await currency.GetStatusAsync(CancellationToken.None)).Changeable);
    }

    [Fact]
    public async Task Currency_change_rechecks_usage_inside_the_versioned_settings_write()
    {
        var usage = new MemoryUsage();
        var currency = new TenantCurrency(new MemorySettings(), Catalogue, [usage]);
        await currency.SetAsync("EUR", CancellationToken.None);
        usage.CountOnCall = call => call >= 2 ? 1 : 0;

        var locked = await Assert.ThrowsAsync<TenantCurrencyLockedException>(() =>
            currency.SetAsync("USD", CancellationToken.None));

        Assert.Equal("EUR", await currency.GetAsync(CancellationToken.None));
        Assert.Equal([new CurrencyUsage("claims", 1)], locked.Usage);
    }

    private sealed class MemoryUsage : ICurrencyUsage
    {
        public long Count { get; set; }
        public Func<int, long>? CountOnCall { get; set; }
        private int calls;
        public Task<IReadOnlyList<CurrencyUsage>> CountAsync(CancellationToken cancellationToken)
        {
            var count = CountOnCall?.Invoke(++calls) ?? Count;
            return Task.FromResult<IReadOnlyList<CurrencyUsage>>(count == 0 ? [] : [new("claims", count)]);
        }
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

        public Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? new VersionedTenantSetting(value, value, DateTime.UnixEpoch) : null);

        public Task<VersionedTenantSetting> SetAsync(string key, string value, string expectedVersion, CancellationToken cancellationToken)
        {
            var current = values.GetValueOrDefault(key);
            if (expectedVersion != (current ?? ITenantSettings.MissingVersion))
                throw new TenantSettingVersionConflictException(key, expectedVersion, current ?? ITenantSettings.MissingVersion);
            values[key] = value;
            return Task.FromResult(new VersionedTenantSetting(value, value, DateTime.UnixEpoch));
        }
    }
}
