using System.Text.Json;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Jurisdictions;

/// <summary>
/// Stable kinds and value shapes only. Jurisdiction values, country membership and commencement dates are tenant data.
/// </summary>
public static class JurisdictionRuleKinds
{
    public static readonly IReadOnlyList<JurisdictionRuleKindRow> All =
    [
        Row(JurisdictionRuleKind.StatutoryConformityTerm, JurisdictionValueType.Json, "months", "Minimum statutory conformity term, including limitation-only metadata."),
        Row(JurisdictionRuleKind.PresumptionPeriod, JurisdictionValueType.Months, "months", "Period during which non-conformity is presumed to have existed."),
        Row(JurisdictionRuleKind.UsedGoodsMinTerm, JurisdictionValueType.Json, "months", "Minimum term for used or refurbished goods and whether agreement is required."),
        Row(JurisdictionRuleKind.ImpliedWarrantyDurationBounds, JurisdictionValueType.Json, "months", "Minimum and maximum implied-warranty duration."),
        Row(JurisdictionRuleKind.RemedyOrder, JurisdictionValueType.Json, null, "Ordered remedies and who chooses the first tier."),
        Row(JurisdictionRuleKind.ShortTermRejectWindow, JurisdictionValueType.Days, "days", "Short-term rejection or refund window."),
        Row(JurisdictionRuleKind.RepairExtensionMonths, JurisdictionValueType.Months, "months", "Term added after an eligible repair."),
        Row(JurisdictionRuleKind.RepairExtensionCount, JurisdictionValueType.WholeNumber, "applications", "Maximum number of repair extensions on one entitlement chain."),
        Row(JurisdictionRuleKind.RefurbishedReplacementRequiresConsent, JurisdictionValueType.Enum, null, "Consent level required before a refurbished replacement may be supplied."),
        Row(JurisdictionRuleKind.RefurbishedLoanAllowed, JurisdictionValueType.Boolean, null, "Whether a refurbished temporary loan is allowed."),
        Row(JurisdictionRuleKind.RepairChoiceNoticeRequired, JurisdictionValueType.Json, null, "Whether the repair-choice notice is required and its language text."),
        Row(JurisdictionRuleKind.ThirdPartyRepairPreservesCover, JurisdictionValueType.Enum, null, "Effect of third-party repair on warranty cover."),
        Row(JurisdictionRuleKind.ProhibitedWording, JurisdictionValueType.PhraseList, null, "Phrases forbidden or requiring review on labels, certificates or warranty documents."),
        Row(JurisdictionRuleKind.MandatoryStatement, JurisdictionValueType.TextLanguage, null, "Mandatory text, language, placement and verbatim requirement."),
        Row(JurisdictionRuleKind.GuaranteeDocumentFields, JurisdictionValueType.StringList, null, "Fields a guarantee or warranty document must disclose."),
        Row(JurisdictionRuleKind.RequiredLanguages, JurisdictionValueType.Json, null, "Required languages, document scope and language precedence."),
        Row(JurisdictionRuleKind.WarrantyDesignation, JurisdictionValueType.Json, null, "Required warranty designation and any threshold."),
        Row(JurisdictionRuleKind.DisclosurePriceThreshold, JurisdictionValueType.Money, null, "Price threshold at which a disclosure duty applies."),
        Row(JurisdictionRuleKind.PrintedExpiryPolicy, JurisdictionValueType.Json, null, "Policy for printing expiry dates and any companion statement."),
        Row(JurisdictionRuleKind.ManufactureDateOnPackage, JurisdictionValueType.Json, null, "Manufacture-date labelling and spare-part exemptions."),
        Row(JurisdictionRuleKind.HarmonisedLabelAsset, JurisdictionValueType.AssetReference, null, "Approved harmonised label asset and its conditions."),
        Row(JurisdictionRuleKind.PartsAndRepairAvailability, JurisdictionValueType.Json, "years", "Parts, firmware and repair availability period and disclosure duty."),
        Row(JurisdictionRuleKind.SecureDeletionCapabilityRequired, JurisdictionValueType.Boolean, null, "Whether the product must provide secure deletion capability."),
        Row(JurisdictionRuleKind.SanitisationBeforeResale, JurisdictionValueType.Json, null, "Sanitisation duty, evidence and standard reference."),
        Row(JurisdictionRuleKind.TakebackDuty, JurisdictionValueType.Json, null, "Electronic-waste take-back duties."),
        Row(JurisdictionRuleKind.HandlerRegistrationRequired, JurisdictionValueType.Json, null, "Handlers that must register and the registry name."),
        Row(JurisdictionRuleKind.EwasteStorageMaxDays, JurisdictionValueType.Json, "days", "Electronic-waste storage limit and permitted extension."),
        Row(JurisdictionRuleKind.BatteryPassportRequired, JurisdictionValueType.Json, null, "Battery passport threshold, class and commencement."),
        Row(JurisdictionRuleKind.RecordRetention, JurisdictionValueType.Json, "months", "Retention period by record class and starting event."),
        Row(JurisdictionRuleKind.ClaimLimitationPeriod, JurisdictionValueType.Json, "months", "Claim limitation period and the event from which it runs."),
        Row(JurisdictionRuleKind.BuyerClassScope, JurisdictionValueType.Enum, null, "Buyer classes protected by the rule."),
    ];

    private static readonly Dictionary<string, JurisdictionRuleKindRow> ByName =
        All.ToDictionary(row => row.Kind, StringComparer.Ordinal);

    public static JurisdictionRuleKindRow Require(string kind) => ByName.TryGetValue(kind, out var row)
        ? row
        : throw new CommandRefusedException("jurisdiction_rule_kind_unknown", $"Rule kind '{kind}' is unknown.");

    public static void ValidateValue(string kind, JsonElement value)
    {
        var definition = Require(kind);
        var valid = definition.ValueType switch
        {
            JurisdictionValueType.Months or JurisdictionValueType.Days or JurisdictionValueType.WholeNumber =>
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0,
            JurisdictionValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            JurisdictionValueType.Enum or JurisdictionValueType.AssetReference =>
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
            JurisdictionValueType.StringList => value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())),
            JurisdictionValueType.PhraseList => ValidPhraseList(value),
            JurisdictionValueType.TextLanguage => ValidText(value),
            JurisdictionValueType.Money => value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number
                && value.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String,
            JurisdictionValueType.Json => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
            _ => false,
        };
        if (!valid) throw new CommandRefusedException("jurisdiction_rule_value_invalid",
            $"The value for '{kind}' does not match its {definition.ValueType} value type.");
    }

    private static bool ValidPhraseList(JsonElement value) => value.ValueKind == JsonValueKind.Array
        && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
            || item.ValueKind == JsonValueKind.Object && item.TryGetProperty("pattern", out var pattern)
            && pattern.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pattern.GetString()));

    private static bool ValidText(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
        && value.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(language.GetString());

    private static JurisdictionRuleKindRow Row(string kind, string valueType, string? unit, string description) =>
        new(kind, valueType, unit, description);
}
