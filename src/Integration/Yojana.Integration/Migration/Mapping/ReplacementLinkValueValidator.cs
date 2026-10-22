using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Mapping;

public sealed class ReplacementLinkValueValidator(ICurrencyCatalogue currencies)
{
    public static ReplacementLinkValues AcceptedDefaults { get; } = new(
        ReplacementDirection.CustomerFacing, ReplacementReason.WarrantyDefect,
        ReplacementLinkState.ClosedReturned, ReplacementConditionAllowed.Any,
        ReplacementPricePolicy.Zero, 0m, "XXX");

    public ReplacementLinkValues Validate(ReplacementLinkValues values)
    {
        var cleaned = values;
        if (!ReplacementDirection.All.Contains(cleaned.Direction))
            throw Error("replacement_direction_unknown", "Direction is not supported.");
        if (!ReplacementReason.All.Contains(cleaned.ReplacementReason))
            throw Error("replacement_reason_unknown", "Replacement reason is not supported.");
        if (!MigratedReplacementLinkState.All.Contains(cleaned.State))
            throw Error("replacement_state_not_terminal", "A migrated replacement link must use a supported terminal state.");
        if (!ReplacementConditionAllowed.All.Contains(cleaned.ReplacementConditionAllowed))
            throw Error("replacement_condition_allowed_unknown", "Replacement condition allowance is not supported.");
        if (!ReplacementPricePolicy.All.Contains(cleaned.PricePolicy))
            throw Error("replacement_price_policy_unknown", "Price policy is not supported.");
        if (cleaned.DifferenceAmount < 0)
            throw Error("replacement_difference_amount_negative", "Difference amount cannot be negative.");
        if (cleaned.PricePolicy == ReplacementPricePolicy.Zero && cleaned.DifferenceAmount != 0)
            throw Error("replacement_zero_price_difference", "A zero-price replacement must have a zero difference amount.");
        if (string.IsNullOrEmpty(cleaned.Currency) || cleaned.Currency.Length != 3 ||
            cleaned.Currency.Any(character => !char.IsAsciiLetterUpper(character)) ||
            cleaned.Currency != "XXX" && currencies.Find(cleaned.Currency) is null)
            throw Error("replacement_currency_invalid", "Currency must be an ISO 4217 code or XXX.");
        if (cleaned.DifferenceAmount > 0 && cleaned.Currency == "XXX")
            throw Error("replacement_currency_required", "A positive difference amount requires a currency other than XXX.");
        return cleaned;
    }

    private static MappingRuleException Error(string rule, string message) => new(rule, message);
}
