using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

internal static class Cx01Money
{
    public static string Code(string currency, Cx01DocumentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency == "XXX")
            throw new Cx01UnmappableException("missing currency in the module event");
        return settings.CurrencyMap?.GetValueOrDefault(currency)
            ?? throw new Cx01UnmappableException($"Currency '{currency}' has no ISO 4217 to remote currency mapping on this connection.");
    }

    public static decimal Round(decimal value, string currency, Cx01DocumentSettings settings) =>
        decimal.Round(value, settings.CurrencyPrecision?.GetValueOrDefault(currency, 2) ?? 2, MidpointRounding.AwayFromZero);

    public static void Apply(IDictionary<string, object?> line, string field, Money money, Cx01DocumentSettings settings)
    {
        var code = Code(money.Currency, settings);
        var amount = Round(money.Amount, money.Currency, settings);
        if (amount <= 0) throw new Cx01DocumentRuleException("Document amounts must be positive after currency rounding.");
        line[field] = amount;
        if (code != settings.LocalCurrencyCode) line["Currency"] = code;
    }
}
