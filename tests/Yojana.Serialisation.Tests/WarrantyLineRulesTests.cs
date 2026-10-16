using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class WarrantyLineRulesTests
{
    [Fact]
    public void Attached_targets_need_a_list_or_a_complete_range()
    {
        var reasons = WarrantyLineRules.ValidateTarget(new WarrantyLineTarget(WarrantyTargetRule.AttachedList), true, true);
        Assert.Equal(["attached_list_required"], reasons);
    }

    [Fact]
    public void Unknown_and_non_serialised_targets_are_both_reported()
    {
        var reasons = WarrantyLineRules.ValidateTarget(new WarrantyLineTarget(WarrantyTargetRule.SameOrder), false, false);
        Assert.Equal(["target_units_unknown", "target_not_serialised"], reasons);
    }

    [Theory]
    [InlineData(3, 0, 0)]
    [InlineData(2, 1, 0, "target_units_unknown")]
    [InlineData(2, 0, 1, "target_not_serialised")]
    public void Resolved_target_counts_drive_case_132_reasons(int resolved, int unknown, int nonSerial, string? expected = null)
    {
        var reasons = WarrantyLineRules.ValidateResolvedTarget(new WarrantyLineTarget(WarrantyTargetRule.SameOrder), resolved, unknown, nonSerial);
        if (expected is null) Assert.Empty(reasons);
        else Assert.Contains(expected, reasons);
    }

    [Fact]
    public void Unregulated_and_unconfigured_jurisdictions_are_allowed()
    {
        Assert.Null(WarrantyLineRules.Regulation(Terms(false), new([]), "DE", "EU", false));
        Assert.Null(WarrantyLineRules.Regulation(Terms(true), new([]), "DE", "EU", false));
    }

    [Fact]
    public void Country_overrides_region_and_required_obligor_evidence_is_enforced()
    {
        var settings = new WarrantyReferencePolicy([new("EU", false), new("DE", true, LicensedObligorRequired: true)]);
        Assert.Equal(WarrantyLineMissingInput.RegulatedJurisdiction, WarrantyLineRules.Regulation(Terms(true), settings, "DE", "EU", false));
        Assert.Null(WarrantyLineRules.Regulation(Terms(true), settings, "DE", "EU", true));
        Assert.Equal(WarrantyLineMissingInput.RegulatedJurisdiction, WarrantyLineRules.Regulation(Terms(true), settings, "FR", "EU", true));
    }

    private static PolicyTerms Terms(bool regulated) => PolicyValidatorTests.Standard24() with { ServiceContractRegulated = regulated };
}
