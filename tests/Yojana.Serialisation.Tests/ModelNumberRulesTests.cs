using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Tests;

public sealed class ModelNumberRulesTests
{
    private static readonly ItemSerialisationProfile Profile = new(Guid.NewGuid(), "HD-20T",
        ItemSerialisationProfile.TrackingModes.Serial, SerialisationPoint.Receipt, "standard_10y",
        ItemSerialisationProfile.ReusePolicies.Never, ItemSerialisationProfile.Completeness.Warn,
        IdentifierScheme.Internal, ModelNumbers: ["HX20PR-64P6ZY0", "ST-4200"]);

    [Theory]
    [InlineData("h d-20t")]
    [InlineData("hx20pr-64p6zy0")]
    [InlineData("H X 2 0 P R - 6 4 P 6 Z Y 0")]
    [InlineData("s t-4200")]
    public void Item_code_and_each_model_number_match_ignoring_case_and_spaces(string value)
    {
        Assert.True(ModelNumberRules.Matches(Profile, IdentifierType.InternalSerial, value));
    }

    [Fact]
    public void Confirmation_requires_a_non_empty_reason()
    {
        var missing = Assert.Throws<CommandRefusedException>(() =>
            ModelNumberRules.RefuseUnlessConfirmed(true, true, "  "));
        Assert.Equal(ModelNumberRules.Rule, missing.Rule);
        ModelNumberRules.RefuseUnlessConfirmed(true, true, "The label has been checked against the chassis.");
    }

    [Fact]
    public void Non_serial_identifier_types_are_not_guarded()
    {
        Assert.False(ModelNumberRules.Matches(Profile, IdentifierType.CustomerAssetTag, "HD-20T"));
        Assert.False(ModelNumberRules.Matches(Profile, IdentifierType.UdiDi, "HX20PR-64P6ZY0"));
    }
}
