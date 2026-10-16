using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class SupersessionRulesTests
{
    [Theory]
    [InlineData(AmendmentReason.Purchase, true)]
    [InlineData(AmendmentReason.Correction, false)]
    [InlineData(AmendmentReason.SellerError, false)]
    [InlineData(AmendmentReason.Goodwill, false)]
    public void Known_reasons_need_manager_and_purchase_needs_its_document(string reason, bool document)
    {
        var required = SupersessionRules.Required(reason, false, false);
        Assert.Equal((YojanaRole.ServiceManager, document, false), (required.Role, required.SourceDocument, required.CustomerNotification));
    }

    [Fact]
    public void A_shorter_term_or_lower_percentage_is_a_reduction()
    {
        var full = new CoverageTerms(100, 100, 100, "on_site");
        Assert.True(SupersessionRules.CoverageReduced(new DateOnly(2029, 1, 1), full, new DateOnly(2028, 1, 1), full));
        Assert.True(SupersessionRules.CoverageReduced(new DateOnly(2029, 1, 1), full, new DateOnly(2030, 1, 1), full with { LabourPct = 80 }));
        Assert.False(SupersessionRules.CoverageReduced(new DateOnly(2029, 1, 1), full, new DateOnly(2030, 1, 1), full));
    }

    [Fact]
    public void A_reduction_with_an_issued_certificate_needs_notification()
    {
        Assert.True(SupersessionRules.Required(AmendmentReason.Correction, true, true).CustomerNotification);
        Assert.False(SupersessionRules.Required(AmendmentReason.Correction, true, false).CustomerNotification);
        Assert.Equal("reason_unknown", Assert.Throws<CommandRefusedException>(() => SupersessionRules.Required("whim", false, false)).Rule);
    }
}
