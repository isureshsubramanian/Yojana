using System.Text.Json;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

internal static class JurisdictionEntitlementRules
{
    public static async Task<int?> UsedGoodsFloorMonthsAsync(IJurisdictionRuleResolver rules, string country,
        string buyerKind, DateOnly contractDate, string? itemClass, IReadOnlyDictionary<string, DateTime> evidence,
        CancellationToken cancellationToken)
    {
        var context = Context(country, buyerKind, contractDate, itemClass, evidence);
        var resolved = await rules.ResolveAsync(JurisdictionRuleKind.UsedGoodsMinTerm, context, cancellationToken);
        return resolved.Count > 0 ? Months(resolved[0].Value) : null;
    }

    public static JurisdictionRuleContext Context(string country, string buyerKind, DateOnly contractDate, string? itemClass,
        IReadOnlyDictionary<string, DateTime>? evidence = null, string? subdivision = null, decimal? price = null,
        string? currency = null) => new(country, subdivision,
            buyerKind == RefurbishedBuyerKind.Consumer ? JurisdictionBuyerClass.Consumer : JurisdictionBuyerClass.Business,
            itemClass, price, currency, contractDate,
            EvidenceDay(evidence, StartTrigger.Invoice) ?? contractDate,
            EvidenceDay(evidence, StartTrigger.Deliver), contractDate);

    public static int? Months(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("months", out var months)
            && months.TryGetInt32(out number)) return number;
        return null;
    }

    public static int? Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) ? number : null;

    private static DateOnly? EvidenceDay(IReadOnlyDictionary<string, DateTime>? evidence, string key) =>
        evidence is not null && evidence.TryGetValue(key, out var at) ? DateOnly.FromDateTime(at) : null;
}
