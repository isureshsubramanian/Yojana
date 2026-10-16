using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T03: the rules a warranty policy version must pass before it is published (errors) or that it is shown (warnings).</summary>
public sealed class PolicyValidatorTests
{
    public static PolicyTerms Standard24() => new(
        new DurationRule(24, DurationRule.Months), DurationBasis.Time, [],
        new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, 14), new StartFallback(StartTrigger.Ship, 21)], 90),
        new CoverageTerms(100, 100, 0, "return_to_base"), ["misuse", "consumables"], ["EU", "UK", "CH"], Transferability.Yes, "full", "STD-90",
        [new RefurbishedTerm(null, "A", "direct", "EU", 12, null), new RefurbishedTerm(null, "C", "portal_consumer", "EU", 6, 12)], 30, AssuranceOrService.Assurance, PolicyTerms.TimeZoneCustomerSite);

    private static PolicyValidationContext Context(DateOnly? previous = null, string family = "base_warranty") => new(
        family,
        new Dictionary<string, (string, bool)> { ["base_warranty"] = ("assurance", true), ["service_level"] = ("service", true), ["retired_family"] = ("service", false) },
        new HashSet<string> { "STD-90" }, new DateOnly(2027, 1, 1), previous);

    private static IEnumerable<string> Errors(PolicyValidation v) => v.Issues.Where(i => i.Severity == PolicyIssue.Error).Select(i => i.Code);

    private static IEnumerable<string> Warnings(PolicyValidation v) => v.Issues.Where(i => i.Severity == PolicyIssue.Warning).Select(i => i.Code);

    [Fact]
    public void The_reference_policy_publishes_with_one_warning_for_the_refurbished_row_below_its_floor()
    {
        var v = PolicyValidator.Validate(Standard24(), Context(new DateOnly(2026, 7, 31)));
        Assert.True(v.CanPublish);
        Assert.Equal(["refurbished_below_floor"], Warnings(v));
    }

    [Fact]
    public void A_new_version_must_apply_from_a_later_contract_date_than_the_published_one()
    {
        Assert.Contains("applies_from_not_later", Errors(PolicyValidator.Validate(Standard24(), Context(new DateOnly(2027, 1, 1)))));
        Assert.Contains("applies_from_not_later", Errors(PolicyValidator.Validate(Standard24(), Context(new DateOnly(2027, 3, 1)))));
        Assert.DoesNotContain("applies_from_not_later", Errors(PolicyValidator.Validate(Standard24(), Context(new DateOnly(2026, 12, 31)))));
    }

    [Fact]
    public void A_start_that_waits_for_late_evidence_needs_a_cap_and_the_chain_never_repeats_a_trigger()
    {
        var uncapped = Standard24() with { Start = new StartRule(StartTrigger.Register, [new StartFallback(StartTrigger.Ship, 30)], null) };
        Assert.Contains("start_cap_missing", Errors(PolicyValidator.Validate(uncapped, Context())));
        var invoiceOnly = Standard24() with { Start = new StartRule(StartTrigger.Invoice, [], null) };
        Assert.DoesNotContain("start_cap_missing", Errors(PolicyValidator.Validate(invoiceOnly, Context())));
        var repeated = Standard24() with { Start = new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice), new StartFallback(StartTrigger.Invoice, 7)], 90) };
        Assert.Contains("fallback_repeated", Errors(PolicyValidator.Validate(repeated, Context())));
        var negative = Standard24() with { Start = new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, -1)], 90) };
        Assert.Contains("fallback_offset_negative", Errors(PolicyValidator.Validate(negative, Context())));
        var unknown = Standard24() with { Start = new StartRule("sunrise", [], 90) };
        Assert.Contains("trigger_unknown", Errors(PolicyValidator.Validate(unknown, Context())));
        var selfExpiry = Standard24() with { Start = new StartRule(StartTrigger.BaseExpiry, [], null) };
        Assert.Contains("base_expiry_on_base", Errors(PolicyValidator.Validate(selfExpiry, Context())));
        Assert.DoesNotContain("base_expiry_on_base", Errors(PolicyValidator.Validate(selfExpiry with { AssuranceOrService = AssuranceOrService.Service }, Context(family: "service_level"))));
    }

    [Theory]
    [InlineData(0, DurationRule.Months, DurationRule.Clamp, "duration_not_positive")]
    [InlineData(12, "weeks", DurationRule.Clamp, "duration_unit_unknown")]
    [InlineData(12, DurationRule.Months, "nearest", "end_of_month_unknown")]
    public void The_duration_rule_is_a_positive_term_in_months_or_days_with_a_known_end_of_month_rule(int value, string unit, string endOfMonth, string code) =>
        Assert.Contains(code, Errors(PolicyValidator.Validate(Standard24() with { Duration = new DurationRule(value, unit, endOfMonth) }, Context())));

    [Fact]
    public void A_usage_based_term_needs_a_usage_limit_and_limits_are_positive_and_unique()
    {
        Assert.Contains("usage_limit_missing", Errors(PolicyValidator.Validate(Standard24() with { DurationBasis = DurationBasis.Both }, Context())));
        var withLimits = Standard24() with { DurationBasis = DurationBasis.Both, UsageLimits = [new UsageLimitTerm("operating_hours", 12000)] };
        Assert.True(PolicyValidator.Validate(withLimits, Context()).CanPublish);
        Assert.Contains("usage_limit_unused", Warnings(PolicyValidator.Validate(withLimits with { DurationBasis = DurationBasis.Time }, Context())));
        Assert.Contains("usage_limit_duplicate", Errors(PolicyValidator.Validate(withLimits with { UsageLimits = [new UsageLimitTerm("km", 1), new UsageLimitTerm("km", 2)] }, Context())));
        Assert.Contains("usage_limit_not_positive", Errors(PolicyValidator.Validate(withLimits with { UsageLimits = [new UsageLimitTerm("km", 0)] }, Context())));
    }

    [Fact]
    public void Coverage_regions_and_reference_data_are_checked()
    {
        Assert.Contains("coverage_pct_range", Errors(PolicyValidator.Validate(Standard24() with { Coverage = new CoverageTerms(120, 100, 0, "on_site") }, Context())));
        Assert.Contains("coverage_empty", Warnings(PolicyValidator.Validate(Standard24() with { Coverage = new CoverageTerms(0, 0, 0, "on_site") }, Context())));
        Assert.Contains("region_scope_empty", Errors(PolicyValidator.Validate(Standard24() with { RegionScope = [] }, Context())));
        Assert.Contains("region_unknown", Errors(PolicyValidator.Validate(Standard24() with { RegionScope = ["Europe"] }, Context())));
        Assert.Contains("replacement_policy_unknown", Errors(PolicyValidator.Validate(Standard24() with { ReplacementPolicyCode = "NONE" }, Context())));
        Assert.Contains("replacement_policy_missing", Warnings(PolicyValidator.Validate(Standard24() with { ReplacementPolicyCode = null }, Context())));
        Assert.Contains("family_unknown", Errors(PolicyValidator.Validate(Standard24(), Context(family: "accidental_damage"))));
        Assert.Contains("family_inactive", Errors(PolicyValidator.Validate(Standard24(), Context(family: "retired_family"))));
        Assert.Contains("accounting_tag_differs", Warnings(PolicyValidator.Validate(Standard24() with { AssuranceOrService = AssuranceOrService.Service }, Context())));
        Assert.Contains("settlement_formula_unknown", Errors(PolicyValidator.Validate(Standard24() with { SettlementFormula = "half" }, Context())));
        Assert.True(PolicyValidator.Validate(Standard24() with { SettlementFormula = "pro_rata(operating_hours)" }, Context()).CanPublish);
        Assert.Contains("time_zone_rule_unknown", Errors(PolicyValidator.Validate(Standard24() with { ReferenceTimeZoneRule = "local" }, Context())));
        Assert.Contains("transferable_unknown", Errors(PolicyValidator.Validate(Standard24() with { Transferable = "maybe" }, Context())));
    }

    [Fact]
    public void Refurbished_rows_are_unique_by_key_and_positive()
    {
        var dup = Standard24() with { Refurbished = [new RefurbishedTerm(null, "B", "direct", "EU", 9, null), new RefurbishedTerm(null, "B", "direct", "EU", 6, null)] };
        Assert.Contains("refurbished_duplicate", Errors(PolicyValidator.Validate(dup, Context())));
        var zero = Standard24() with { Refurbished = [new RefurbishedTerm(null, "B", "direct", "EU", 0, null)] };
        Assert.Contains("refurbished_duration_not_positive", Errors(PolicyValidator.Validate(zero, Context())));
    }
}
