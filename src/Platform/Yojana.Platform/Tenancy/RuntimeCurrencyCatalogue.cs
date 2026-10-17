using System.Globalization;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>
/// ISO 4217 currencies as the .NET runtime's globalisation data knows them (one entry per code, from the regions that
/// use it). No package or bundled table is involved; a currency the runtime does not know cannot be chosen.
/// </summary>
public sealed class RuntimeCurrencyCatalogue : ICurrencyCatalogue
{
    private readonly Dictionary<string, CurrencyInfo> byCode;

    public RuntimeCurrencyCatalogue()
    {
        byCode = new Dictionary<string, CurrencyInfo>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            RegionInfo region;
            try { region = new RegionInfo(culture.Name); }
            catch (ArgumentException) { continue; }
            var code = ITenantCurrency.Normalise(region.ISOCurrencySymbol);
            if (code is null || byCode.ContainsKey(code)) continue;
            byCode[code] = new CurrencyInfo(code, region.CurrencyEnglishName, region.CurrencySymbol, culture.NumberFormat.CurrencyDecimalDigits);
        }

        All = byCode.Values.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<CurrencyInfo> All { get; }

    public CurrencyInfo? Find(string? code) => ITenantCurrency.Normalise(code) is { } normalised && byCode.TryGetValue(normalised, out var info) ? info : null;
}
