using System.Globalization;
using System.Text.Json;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// The single parser for entitlement bulk-operation arguments. Keeping this pure makes malformed input fail before a
/// selection is inspected and prevents the preview and apply paths from assigning different meanings to the same key.
/// </summary>
public static class EntitlementBulkArguments
{
    public static Common Parse(BulkOperationArguments arguments)
    {
        var extra = arguments.Extra ?? new Dictionary<string, string>();
        return new Common(
            Required(extra, "family"),
            OptionalGuid(extra, "policy_version_id"),
            Optional(extra, "policy_code"),
            OptionalDate(extra, "contract_date"),
            OptionalDate(extra, "effective_date"),
            OptionalDate(extra, "new_start"),
            OptionalDate(extra, "new_end"),
            OptionalInt(extra, "new_term_value"),
            Optional(extra, "new_term_unit"),
            OptionalJson<CoverageTerms>(extra, "coverage"),
            OptionalGuid(extra, "obligor_entity_id"),
            OptionalGuid(extra, "owner_party_ref"),
            Optional(extra, "owner_party_type"),
            OptionalGuid(extra, "payer_party_ref"),
            Optional(extra, "payer_party_type"),
            OptionalDecimal(extra, "price"),
            Optional(extra, "currency"),
            Optional(extra, "customer_notification_ref"),
            Optional(extra, "finance_review_ref"),
            OptionalDecimal(extra, "entered_value"),
            List(extra, "evidence_refs"),
            Optional(extra, "country"),
            OptionalGuid(extra, "site_location_id"),
            Evidence(extra));
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        Optional(values, key) ?? throw Refused(key, "is required");

    private static string? Optional(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static Guid? OptionalGuid(IReadOnlyDictionary<string, string> values, string key) => Optional(values, key) is not { } value ? null
        : Guid.TryParse(value, out var parsed) ? parsed : throw Refused(key, "must be a UUID");

    private static DateOnly? OptionalDate(IReadOnlyDictionary<string, string> values, string key) => Optional(values, key) is not { } value ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : throw Refused(key, "must be yyyy-MM-dd");

    private static int? OptionalInt(IReadOnlyDictionary<string, string> values, string key) => Optional(values, key) is not { } value ? null
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Refused(key, "must be an integer");

    private static decimal? OptionalDecimal(IReadOnlyDictionary<string, string> values, string key) => Optional(values, key) is not { } value ? null
        : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Refused(key, "must be a decimal number");

    private static T? OptionalJson<T>(IReadOnlyDictionary<string, string> values, string key) where T : class
    {
        if (Optional(values, key) is not { } value) return null;
        try { return JsonSerializer.Deserialize<T>(value, Ledger.LedgerJson.Options) ?? throw Refused(key, "must not be null"); }
        catch (JsonException) { throw Refused(key, "must be valid JSON"); }
    }

    private static string[] List(IReadOnlyDictionary<string, string> values, string key) => Optional(values, key) is not { } value
        ? [] : value.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, DateTime> Evidence(IReadOnlyDictionary<string, string> values)
    {
        if (Optional(values, "evidence") is not { } value) return new Dictionary<string, DateTime>();
        try { return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(value, Ledger.LedgerJson.Options) ?? new Dictionary<string, DateTime>(); }
        catch (JsonException) { throw Refused("evidence", "must be a JSON object of trigger to UTC instant"); }
    }

    private static CommandRefusedException Refused(string key, string problem) => new("bulk_arguments_invalid", $"Bulk argument '{key}' {problem}.");

    public sealed record Common(string Family, Guid? PolicyVersionId, string? PolicyCode, DateOnly? ContractDate, DateOnly? EffectiveDate,
        DateOnly? NewStart, DateOnly? NewEnd, int? NewTermValue, string? NewTermUnit, CoverageTerms? Coverage, Guid? ObligorEntityId,
        Guid? OwnerPartyRef, string? OwnerPartyType, Guid? PayerPartyRef, string? PayerPartyType, decimal? Price, string? Currency,
        string? CustomerNotificationRef, string? FinanceReviewRef, decimal? EnteredValue, IReadOnlyList<string> EvidenceRefs,
        string? Country, Guid? SiteLocationId, IReadOnlyDictionary<string, DateTime> Evidence);
}
