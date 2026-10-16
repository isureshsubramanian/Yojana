using System.Globalization;
using System.Text.RegularExpressions;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>What the validator needs from the database, read by the caller so the rules stay a pure function.</summary>
public sealed record PolicyValidationContext(
    string CoverageFamily,
    IReadOnlyDictionary<string, (string AccountingTag, bool Active)> Families,
    IReadOnlySet<string> ReplacementPolicyCodes,
    DateOnly AppliesToContractsFrom,
    DateOnly? PreviousVersionFrom,
    string? TenantCurrency = null);

/// <summary>
/// The rules a policy version must satisfy before it can be published (P2-T03; the Validation card of the policy editor).
/// Errors stop publication; warnings are shown and accepted. Every issue names the field it belongs to.
/// </summary>
public static partial class PolicyValidator
{
    private static readonly string[] Triggers = [StartTrigger.Receipt, StartTrigger.Ship, StartTrigger.Deliver, StartTrigger.Install, StartTrigger.Register, StartTrigger.Invoice, StartTrigger.ConsumptionFromConsignment, StartTrigger.FirstExternalSale, StartTrigger.PlanPurchase, StartTrigger.BaseExpiry];

    // Triggers whose evidence can arrive long after the sale: a start from them needs a cap (ADR-0001 Terms, start_cap).
    private static readonly string[] LateTriggers = [StartTrigger.Install, StartTrigger.Register, StartTrigger.Deliver, StartTrigger.FirstExternalSale, StartTrigger.ConsumptionFromConsignment];

    private static readonly string[] RegionGroups = ["EU", "EEA", "EFTA", "UK", "WORLD"];

    public static PolicyValidation Validate(PolicyTerms terms, PolicyValidationContext context)
    {
        var issues = new List<PolicyIssue>();
        void Error(string code, string field, string message) => issues.Add(new PolicyIssue(PolicyIssue.Error, code, field, message));
        void Warn(string code, string field, string message) => issues.Add(new PolicyIssue(PolicyIssue.Warning, code, field, message));

        if (!context.Families.TryGetValue(context.CoverageFamily, out var family)) Error("family_unknown", "coverage_family", $"Coverage family '{context.CoverageFamily}' is not defined for this tenant.");
        else if (!family.Active) Error("family_inactive", "coverage_family", $"Coverage family '{context.CoverageFamily}' is inactive.");
        else if (!string.Equals(family.AccountingTag.Split(' ')[0], terms.AssuranceOrService, StringComparison.Ordinal)) Warn("accounting_tag_differs", "assurance_or_service", $"The family is tagged '{family.AccountingTag}' but the policy says '{terms.AssuranceOrService}'.");

        if (context.PreviousVersionFrom is { } previous && context.AppliesToContractsFrom <= previous)
        {
            Error("applies_from_not_later", "applies_to_contracts_from", $"Contracts from {Day(context.AppliesToContractsFrom)} would overlap the published version for contracts from {Day(previous)}; a new version must start later.");
        }

        var d = terms.Duration;
        if (d.Value <= 0) Error("duration_not_positive", "duration", "The term must be at least one day or month.");
        if (d.Unit is not (DurationRule.Months or DurationRule.Days)) Error("duration_unit_unknown", "duration", $"Unit '{d.Unit}' is not months or days.");
        if (d.EndOfMonth is not (DurationRule.Clamp or DurationRule.Roll)) Error("end_of_month_unknown", "duration", $"End-of-month rule '{d.EndOfMonth}' is not clamp or roll.");
        if (d.Unit == DurationRule.Days && d.EndOfMonth == DurationRule.Roll) Warn("end_of_month_unused", "duration", "A term in days never meets the end-of-month rule.");
        else if (d.EndOfMonth == DurationRule.Roll) Warn("end_of_month_roll", "duration", "Roll is not monotonic: a unit started on 31 January ends after one started on 1 February. Clamp avoids it.");

        if (terms.DurationBasis is not (DurationBasis.Time or DurationBasis.Usage or DurationBasis.Both)) Error("duration_basis_unknown", "duration_basis", $"Duration basis '{terms.DurationBasis}' is not time, usage or both.");
        else if (terms.DurationBasis != DurationBasis.Time && terms.UsageLimits.Count == 0) Error("usage_limit_missing", "usage_limits", "A usage-based term needs at least one usage limit.");
        else if (terms.DurationBasis == DurationBasis.Time && terms.UsageLimits.Count > 0) Warn("usage_limit_unused", "usage_limits", "Usage limits are recorded but the term ends on time alone.");
        foreach (var dup in terms.UsageLimits.GroupBy(u => u.MeterType).Where(g => g.Count() > 1)) Error("usage_limit_duplicate", "usage_limits", $"Meter '{dup.Key}' is limited twice.");
        if (terms.UsageLimits.Any(u => u.Limit <= 0)) Error("usage_limit_not_positive", "usage_limits", "Every usage limit must be above zero.");

        var start = terms.Start;
        if (!Triggers.Contains(start.Trigger)) Error("trigger_unknown", "start.trigger", $"Start trigger '{start.Trigger}' is not known.");
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Trigger };
        foreach (var fallback in start.Fallbacks)
        {
            if (!Triggers.Contains(fallback.Trigger)) Error("trigger_unknown", "start.fallbacks", $"Fallback trigger '{fallback.Trigger}' is not known.");
            if (!seen.Add(fallback.Trigger)) Error("fallback_repeated", "start.fallbacks", $"'{fallback.Trigger}' appears twice in the start chain.");
            if (fallback.OffsetDays < 0) Error("fallback_offset_negative", "start.fallbacks", $"The offset after '{fallback.Trigger}' cannot be negative.");
        }

        var late = seen.Any(t => LateTriggers.Contains(t));
        if (start.CapDays is null && late) Error("start_cap_missing", "start.cap_days", "A start from delivery, installation or registration can arrive months later; set a cap so the start is never later than invoice plus N days.");
        if (start.CapDays is <= 0) Error("start_cap_not_positive", "start.cap_days", "The cap must be at least one day.");
        if (start.Trigger == StartTrigger.BaseExpiry && context.CoverageFamily == CoverageFamilyCode.Base) Error("base_expiry_on_base", "start.trigger", "The base warranty cannot start at its own expiry.");

        // A plan sold after the sale (P2-T07): base_expiry follows the eligibility rule's base family, so it needs one.
        if (start.Trigger == StartTrigger.BaseExpiry && terms.Eligibility is null) Error("base_family_missing", "eligibility", "A plan that starts at the base warranty's expiry must name the base family in its eligibility rule.");
        if (terms.Eligibility is { } eligibility)
        {
            if (eligibility.Kind is not (EligibilityRule.WithinBaseCoverage or EligibilityRule.WithinDaysOfBaseStart)) Error("eligibility_unknown", "eligibility.kind", $"'{eligibility.Kind}' is not within_base_coverage or within_days_of_base_start.");
            if (eligibility.Kind == EligibilityRule.WithinDaysOfBaseStart && eligibility.WithinDays is not > 0) Error("eligibility_days_not_positive", "eligibility.within_days", "The purchase window must be at least one day.");
            if (eligibility.BaseFamily == context.CoverageFamily) Error("eligibility_base_is_self", "eligibility.base_family", "A plan cannot depend on its own family.");
            else if (!context.Families.ContainsKey(eligibility.BaseFamily)) Error("family_unknown", "eligibility.base_family", $"Coverage family '{eligibility.BaseFamily}' is not defined for this tenant.");
        }

        if (start.Trigger is StartTrigger.PlanPurchase or StartTrigger.BaseExpiry && terms.AssuranceOrService == AssuranceOrService.Assurance)
        {
            Warn("plan_trigger_on_assurance", "start.trigger", "A plan bought separately is a service warranty; an assurance warranty comes with the sale and starts from it.");
        }

        var c = terms.Coverage;
        foreach (var (name, pct) in new[] { ("parts_pct", c.PartsPct), ("labour_pct", c.LabourPct), ("travel_pct", c.TravelPct) })
        {
            if (pct is < 0 or > 100) Error("coverage_pct_range", "coverage." + name, $"{name} must be between 0 and 100.");
        }

        if (c.PartsPct == 0 && c.LabourPct == 0 && c.TravelPct == 0) Warn("coverage_empty", "coverage", "The policy covers no parts, labour or travel.");
        if (c.ResponseHours is <= 0) Error("response_hours_not_positive", "coverage.response_hours", "A response time must be at least one hour.");

        if (terms.RegionScope.Count == 0) Error("region_scope_empty", "region_scope", "Name at least one country or region group.");
        foreach (var region in terms.RegionScope.Where(r => !IsRegion(r))) Error("region_unknown", "region_scope", $"'{region}' is neither an ISO 3166 alpha-2 code nor one of {string.Join(", ", RegionGroups)}.");

        if (terms.Transferable is not (Transferability.Yes or Transferability.No or Transferability.Fee or Transferability.RegistrationRequired)) Error("transferable_unknown", "transferable", $"'{terms.Transferable}' is not a transferability value.");
        if (!SettlementFormula().IsMatch(terms.SettlementFormula)) Error("settlement_formula_unknown", "settlement_formula", $"'{terms.SettlementFormula}' is not full or pro_rata(measure).");
        if (terms.ReplacementPolicyCode is { } rp && !context.ReplacementPolicyCodes.Contains(rp)) Error("replacement_policy_unknown", "replacement_policy", $"Replacement policy '{rp}' is not defined for this tenant.");
        if (terms.ReplacementPolicyCode is null && context.CoverageFamily == CoverageFamilyCode.Base) Warn("replacement_policy_missing", "replacement_policy", "No replacement policy: a replaced unit's entitlement will need the tenant default.");

        foreach (var dup in terms.Refurbished.GroupBy(r => (r.ItemId, r.Grade, r.Channel, r.Region)).Where(g => g.Count() > 1))
        {
            Error("refurbished_duplicate", "refurbished", $"Grade {dup.Key.Grade}, {dup.Key.Channel}, {dup.Key.Region} is defined twice.");
        }

        foreach (var r in terms.Refurbished)
        {
            if (r.DurationMonths <= 0) Error("refurbished_duration_not_positive", "refurbished", $"Grade {r.Grade}, {r.Channel}, {r.Region}: the term must be at least one month.");
            if (!IsRegion(r.Region)) Error("region_unknown", "refurbished", $"Grade {r.Grade}: '{r.Region}' is not a region.");
            if (r.StatutoryFloorMonths is { } floor && r.DurationMonths < floor)
            {
                Warn("refurbished_below_floor", "refurbished", $"Grade {r.Grade}, {r.Channel}, {r.Region}: {r.DurationMonths} months is below the statutory floor of {floor} months; the floor will apply.");
            }
            if (!RefurbishedWarrantyStatement.All.Contains(r.StatementKind)) Error("refurbished_statement_unknown", "refurbished", $"Grade {r.Grade}: '{r.StatementKind}' is not a warranty statement kind.");
        }

        if (terms.DoaWindowDays is < 0) Error("doa_window_negative", "doa_window_days", "The dead-on-arrival window cannot be negative.");
        if (terms.AssuranceOrService is not (AssuranceOrService.Assurance or AssuranceOrService.Service)) Error("accounting_tag_unknown", "assurance_or_service", $"'{terms.AssuranceOrService}' is not assurance or service.");
        if (terms.ReferenceTimeZoneRule is not (PolicyTerms.TimeZoneCustomerSite or PolicyTerms.TimeZoneObligorEntity or PolicyTerms.TimeZoneUtc)) Error("time_zone_rule_unknown", "reference_time_zone_rule", $"'{terms.ReferenceTimeZoneRule}' is not customer_site, obligor_entity or utc.");
        if (terms.ServiceContractRegulated && terms.AssuranceOrService == AssuranceOrService.Assurance) Warn("regulated_assurance", "service_contract_regulated", "Service-contract regulation applies to separately priced plans; this policy is an assurance warranty.");

        // P2-T08b: the value is in the tenant's currency, so it cannot be checked, or used, before that is set up.
        if (terms.AmendmentValue is { } value)
        {
            if (value.Amount <= 0) Error("amendment_value_not_positive", "amendment_value.amount", "A day or month of term must be worth more than zero.");
            if (value.Per is not (AmendmentValueTerm.PerDay or AmendmentValueTerm.PerMonth)) Error("amendment_value_per_unknown", "amendment_value.per", $"'{value.Per}' is not day or month.");
            if (context.TenantCurrency is null) Error("currency_not_configured", "amendment_value.currency", "Set up the tenant currency before giving the policy a value.");
            else if (!string.Equals(value.Currency, context.TenantCurrency, StringComparison.Ordinal)) Error("amendment_value_currency_differs", "amendment_value.currency", $"The value is in {value.Currency}; the tenant works in {context.TenantCurrency}.");
        }

        return new PolicyValidation(issues);
    }

    private static bool IsRegion(string value) => RegionGroups.Contains(value) || (value.Length == 2 && value.All(char.IsAsciiLetterUpper));

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(full|pro_rata\([a-z_]+\))$", RegexOptions.CultureInvariant)]
    private static partial Regex SettlementFormula();
}
