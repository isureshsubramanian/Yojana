using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>The tenant's working currency over <see cref="ITenantSettings"/> (key <see cref="ITenantCurrency.Key"/>).</summary>
public sealed class TenantCurrency(ITenantSettings settings) : ITenantCurrency
{
    public async Task<string?> GetAsync(CancellationToken cancellationToken) => ITenantCurrency.Normalise(await settings.GetAsync(ITenantCurrency.Key, cancellationToken));

    public async Task<string> SetAsync(string code, CancellationToken cancellationToken)
    {
        var normalised = ITenantCurrency.Normalise(code) ?? throw new ArgumentException($"'{code}' is not an ISO 4217 currency code (three letters).", nameof(code));
        await settings.SetAsync(ITenantCurrency.Key, normalised, cancellationToken);
        return normalised;
    }
}
