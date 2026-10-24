using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>The tenant's home currency over <see cref="ITenantSettings"/>, validated against the catalogue and module usage ports.</summary>
public sealed class TenantCurrency(ITenantSettings settings, ICurrencyCatalogue catalogue, IEnumerable<ICurrencyUsage> usages) : ITenantCurrency
{
    public async Task<string?> GetAsync(CancellationToken cancellationToken) => ITenantCurrency.Normalise(await settings.GetAsync(ITenantCurrency.Key, cancellationToken));

    public async Task<string> SetAsync(string code, CancellationToken cancellationToken)
    {
        var known = catalogue.Find(code) ?? throw new ArgumentException($"'{code}' is not an ISO 4217 currency in the catalogue.", nameof(code));
        var current = await GetAsync(cancellationToken);
        if (current == known.Code) return current;
        if (current is not null)
        {
            var recorded = await UsageAsync(cancellationToken);
            if (recorded.Count > 0) throw new TenantCurrencyLockedException(current, known.Code, recorded);
        }

        // The version check is the Platform side of the currency/first-amount race. Money writers re-read the home
        // currency immediately before recording; separating either side permits a concurrent change to relabel an amount.
        var versioned = await settings.GetVersionedAsync(ITenantCurrency.Key, cancellationToken);
        var recordedInsideWrite = await UsageAsync(cancellationToken);
        if (current is not null && recordedInsideWrite.Count > 0)
            throw new TenantCurrencyLockedException(current, known.Code, recordedInsideWrite);
        await settings.SetAsync(ITenantCurrency.Key, known.Code, versioned?.Version ?? ITenantSettings.MissingVersion,
            cancellationToken);
        return known.Code;
    }

    public async Task<TenantCurrencyStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var currency = await GetAsync(cancellationToken);
        var usage = await UsageAsync(cancellationToken);
        return new(currency, usage.Count == 0, usage);
    }

    private async Task<IReadOnlyList<CurrencyUsage>> UsageAsync(CancellationToken cancellationToken)
    {
        var result = new List<CurrencyUsage>();
        foreach (var usage in usages)
            result.AddRange(await usage.CountAsync(cancellationToken));
        return result.Where(value => value.Count > 0).OrderBy(value => value.Kind, StringComparer.Ordinal).ToList();
    }
}
