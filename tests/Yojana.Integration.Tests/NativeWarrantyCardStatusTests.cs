using Yojana.Integration.Coexistence;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Contracts;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Tests;

public sealed class NativeWarrantyCardStatusTests
{
    [Theory]
    [InlineData(LifecycleStatus.Released, UnitDisposition.Active, LocationKind.Warehouse, EpisodeReason.Receipt,
        NativeWarrantyCardStatus.Active)]
    [InlineData(LifecycleStatus.Released, UnitDisposition.InProgress, LocationKind.InRepair, EpisodeReason.Return,
        NativeWarrantyCardStatus.InRepair)]
    [InlineData(LifecycleStatus.Released, UnitDisposition.Returned, LocationKind.Quarantine, EpisodeReason.Loan,
        NativeWarrantyCardStatus.Returned)]
    [InlineData(LifecycleStatus.Released, UnitDisposition.Active, LocationKind.CustomerSite, EpisodeReason.Loan,
        NativeWarrantyCardStatus.Loaned)]
    [InlineData(LifecycleStatus.Void, UnitDisposition.InProgress, LocationKind.InRepair, EpisodeReason.Loan,
        NativeWarrantyCardStatus.Terminated)]
    public void Module_facts_follow_the_single_precedence_rule(string lifecycle, string disposition,
        string locationKind, string custodyReason, string expected)
    {
        var source = new NativeWarrantyPushSource(lifecycle, disposition, locationKind, custodyReason,
            "FIC-SERVER-100", null);
        Assert.Equal(expected, NativeWarrantyCardStatusDerivation.Derive(source));
    }

    [Fact]
    public void Configured_remote_codes_must_not_be_empty()
    {
        Assert.Throws<InvalidOperationException>(() => new Cx01NativeWarrantyStatusCodes(InRepair: " ").Validate());
        Assert.Null(new Cx01NativeWarrantyStatusCodes(InRepair: null).Validate().InRepair);
    }
}
