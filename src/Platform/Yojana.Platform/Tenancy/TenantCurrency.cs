using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>The tenant's working and accepted currencies over <see cref="ITenantSettings"/>, validated against the catalogue.</summary>
public sealed class TenantCurrency(ITenantSettings settings, ICurrencyCatalogue catalogue) : ITenantCurrency
{
    public async Task<string?> GetAsync(CancellationToken cancellationToken) => ITenantCurrency.Normalise(await settings.GetAsync(ITenantCurrency.Key, cancellationToken));

    public async Task<string> SetAsync(string code, CancellationToken cancellationToken)
    {
        var known = catalogue.Find(code) ?? throw new ArgumentException($"'{code}' is not an ISO 4217 currency in the catalogue.", nameof(code));
        await settings.SetAsync(ITenantCurrency.Key, known.Code, cancellationToken);
        return known.Code;
    }

    public async Task<IReadOnlyList<string>> GetAcceptedAsync(CancellationToken cancellationToken)
    {
        var working = await GetAsync(cancellationToken);
        if (working is null) return [];
        var text = await settings.GetAsync(ITenantCurrency.AcceptedKey, cancellationToken);
        var others = text is null ? [] : JsonSerializer.Deserialize<List<string>>(text) ?? [];
        return [working, .. others.Select(ITenantCurrency.Normalise).OfType<string>().Where(c => c != working).Distinct().Order(StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<string>> SetAcceptedAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var known = codes.Select(code => catalogue.Find(code)?.Code ?? throw new ArgumentException($"'{code}' is not an ISO 4217 currency in the catalogue.", nameof(codes)))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        await settings.SetAsync(ITenantCurrency.AcceptedKey, JsonSerializer.Serialize(known), cancellationToken);
        return await GetAcceptedAsync(cancellationToken);
    }
}
