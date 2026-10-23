using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Portal;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class CustomerAssertionCustodyRuleTests
{
    private static readonly Guid PreviousOwner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherParty = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(true, true, LocationKind.CustomerSite, true)]
    [InlineData(true, false, LocationKind.CustomerSite, false)]
    [InlineData(true, true, LocationKind.Warehouse, false)]
    [InlineData(true, false, LocationKind.At3pl, false)]
    [InlineData(false, false, LocationKind.CustomerSite, false)]
    public void Default_moves_only_from_the_previous_owners_customer_site(
        bool hasPreviousOwner,
        bool heldByPreviousOwner,
        string locationKind,
        bool expected)
    {
        var owner = hasPreviousOwner ? PreviousOwner : (Guid?)null;
        var holder = heldByPreviousOwner ? PreviousOwner : OtherParty;

        Assert.Equal(expected, CustomerAssertionCustodyRule.DefaultFor(owner, holder, locationKind));
    }

    [Theory]
    [InlineData(true, LocationKind.CustomerSite, false)]
    [InlineData(false, LocationKind.CustomerSite, true)]
    [InlineData(false, LocationKind.Warehouse, false)]
    [InlineData(false, LocationKind.At3pl, false)]
    public void Mismatch_warning_applies_only_to_another_partys_customer_site(
        bool sameParty,
        string locationKind,
        bool expected)
    {
        Assert.Equal(expected, CustomerAssertionCustodyRule.OwnerAndHolderDiffer(
            PreviousOwner, sameParty ? PreviousOwner : OtherParty, locationKind));
    }
}
