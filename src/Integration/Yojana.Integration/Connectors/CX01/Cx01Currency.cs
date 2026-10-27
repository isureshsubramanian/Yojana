using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>The active and reserved alphabetic codes recognised by the ISO 4217 list.</summary>
internal static class Iso4217Codes
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "AUD", "AWG", "AZN",
        "BAM", "BBD", "BDT", "BGN", "BHD", "BIF", "BMD", "BND", "BOB", "BOV",
        "BRL", "BSD", "BTN", "BWP", "BYN", "BZD", "CAD", "CDF", "CHE", "CHF",
        "CHW", "CLF", "CLP", "CNY", "COP", "COU", "CRC", "CUP", "CVE", "CZK",
        "DJF", "DKK", "DOP", "DZD", "EGP", "ERN", "ETB", "EUR", "FJD", "FKP",
        "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD", "HKD", "HNL",
        "HTG", "HUF", "IDR", "ILS", "INR", "IQD", "IRR", "ISK", "JMD", "JOD",
        "JPY", "KES", "KGS", "KHR", "KMF", "KPW", "KRW", "KWD", "KYD", "KZT",
        "LAK", "LBP", "LKR", "LRD", "LSL", "LYD", "MAD", "MDL", "MGA", "MKD",
        "MMK", "MNT", "MOP", "MRU", "MUR", "MVR", "MWK", "MXN", "MXV", "MYR",
        "MZN", "NAD", "NGN", "NIO", "NOK", "NPR", "NZD", "OMR", "PAB", "PEN",
        "PGK", "PHP", "PKR", "PLN", "PYG", "QAR", "RON", "RSD", "RUB", "RWF",
        "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD",
        "SSP", "STN", "SVC", "SYP", "SZL", "THB", "TJS", "TMT", "TND", "TOP",
        "TRY", "TTD", "TWD", "TZS", "UAH", "UGX", "USD", "USN", "UYI", "UYU",
        "UYW", "UZS", "VED", "VES", "VND", "VUV", "WST", "XAF", "XAG", "XAU",
        "XBA", "XBB", "XBC", "XBD", "XCD", "XCG", "XDR", "XOF", "XPD", "XPF",
        "XPT", "XSU", "XTS", "XUA", "XXX", "YER", "ZAR", "ZMW", "ZWG",
    };

    public static bool Contains(string? code) => code is not null && Codes.Contains(code);
}

internal static class Cx01CurrencyNames
{
    private static readonly IReadOnlyDictionary<string, string> Suggestions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["US Dollar"] = "USD",
            ["United States Dollar"] = "USD",
            ["Canadian Dollar"] = "CAD",
            ["Euro"] = "EUR",
            ["British Pound"] = "GBP",
            ["Pound Sterling"] = "GBP",
            ["Indian Rupee"] = "INR",
            ["Japanese Yen"] = "JPY",
            ["Australian Dollar"] = "AUD",
            ["New Zealand Dollar"] = "NZD",
            ["Singapore Dollar"] = "SGD",
            ["Swiss Franc"] = "CHF",
            ["Chinese Yuan"] = "CNY",
        };

    public static string? Suggest(string? name) => name is null ? null
        : Suggestions.GetValueOrDefault(name.Trim());
}

public sealed record Cx01SurveyCurrency(
    string ErpCode,
    string Name,
    string? DocumentsCode,
    string? SuggestedIsoCode);

public sealed record Cx01CurrencyMapEntry(
    string ErpCode,
    string Name,
    string? DocumentsCode,
    string? SuggestedIsoCode,
    string? MappedIsoCode);

public sealed record Cx01CurrencyMapView(
    Guid ConnectionId,
    DateTime? SurveyObservedAt,
    IReadOnlyList<Cx01CurrencyMapEntry> Currencies);

public sealed record ConfirmCx01CurrencyMapping(string ErpCode, string IsoCode);

public sealed class Cx01CurrencyMapService(
    Cx01Settings settings,
    Cx01SurveyService survey,
    ITenantContext tenant,
    IClock clock,
    IConnectionAuditSink audit)
{
    public async Task<Cx01CurrencyMapView> GetAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var latest = await survey.LatestAsync(connectionId, cancellationToken);
        var rows = latest?.Currencies ?? [];
        return new(connectionId, latest?.ObservedAt, rows.OrderBy(x => x.ErpCode, StringComparer.Ordinal)
            .Select(row => new Cx01CurrencyMapEntry(row.ErpCode, row.Name, row.DocumentsCode,
                row.SuggestedIsoCode, configured.CurrencyMap?.GetValueOrDefault(row.ErpCode)))
            .ToList());
    }

    public async Task<Cx01CurrencyMapView> ConfirmAsync(Guid connectionId, ConfirmCx01CurrencyMapping request,
        string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new CommandRefusedException("audit_actor_required", "An authenticated actor is required.");
        var erpCode = request.ErpCode?.Trim() ?? "";
        var isoCode = request.IsoCode?.Trim().ToUpperInvariant() ?? "";
        if (erpCode.Length is < 1 or > 16)
            throw new CommandRefusedException("erp_currency_code_invalid", "The ERP currency code is invalid.");
        if (!Iso4217Codes.Contains(isoCode))
            throw new CommandRefusedException("iso_currency_code_invalid", $"'{isoCode}' is not an ISO 4217 currency code.");

        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var mappings = new Dictionary<string, string>(configured.CurrencyMap
            ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var before = mappings.GetValueOrDefault(erpCode);
        mappings[erpCode] = isoCode;
        await settings.SetAsync(connectionId, configured with { CurrencyMap = mappings }, cancellationToken);
        if (!StringComparer.Ordinal.Equals(before, isoCode))
            await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(), connectionId,
                ConnectionAuditEventType.CurrencySettingsChanged, actor, clock.UtcNow,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    erp_currency_code = erpCode,
                    previous_iso_code = before,
                    mapped_iso_code = isoCode,
                })), cancellationToken);
        return await GetAsync(connectionId, cancellationToken);
    }
}
