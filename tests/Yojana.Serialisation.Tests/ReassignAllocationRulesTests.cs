using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Tests;

public sealed class ReassignAllocationRulesTests
{
    [Fact]
    public void Same_item_and_scheme_are_compatible()
    {
        var item = Guid.NewGuid();
        var source = new ProductionOrderAllocation(new("production_order", Guid.NewGuid()), item, "internal-monthly", 40);
        var target = new ProductionOrderAllocation(new("production_order", Guid.NewGuid()), item, "internal-monthly", 40);

        Assert.Null(ReassignAllocationRules.CompatibilityProblem(source, target));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_different_item_or_scheme_is_refused(bool differentItem, bool differentScheme)
    {
        var item = Guid.NewGuid();
        var source = new ProductionOrderAllocation(new("production_order", Guid.NewGuid()), item, "internal", 40);
        var target = new ProductionOrderAllocation(new("production_order", Guid.NewGuid()), differentItem ? Guid.NewGuid() : item,
            differentScheme ? "vendor" : "internal", 40);

        var refusal = ReassignAllocationRules.CompatibilityProblem(source, target);

        Assert.Equal("target_order_item_or_scheme_differs", refusal!.Rule);
        Assert.NotNull(refusal.UnblockingStep);
    }

    [Fact]
    public void Quantity_refusal_names_the_owner_approved_unblocking_step()
    {
        Assert.Equal("exchange instead or raise the order quantity in Manufacturing", ReassignAllocationRules.QuantityUnblockingStep);
    }

    [Fact]
    public void Document_identity_includes_type_id_and_line()
    {
        var id = Guid.NewGuid();
        Assert.True(ReassignAllocationRules.SameDocument(new("production_order", id, 2), new("production_order", id, 2)));
        Assert.False(ReassignAllocationRules.SameDocument(new("production_order", id, 2), new("production_order", id, 3)));
    }
}
