using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class EntitlementRequirementRulesTests
{
    [Theory]
    [InlineData(false, true, EntitlementMissingInput.NoStartEvidence)]
    [InlineData(true, false, EntitlementMissingInput.CreationFailed)]
    [InlineData(true, true, null)]
    public void Resolution_names_the_first_missing_input(bool document, bool obligor, string? expected) =>
        Assert.Equal(expected, EntitlementRequirementRules.MissingInput(document, obligor));
}
