using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Tests;

public sealed class LateSerializationRulesTests
{
    private static readonly Guid Location = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DocumentRef Document = new("stocktake", Guid.Parse("20000000-0000-0000-0000-000000000001"));

    [Fact]
    public void Every_open_unit_must_have_a_serial_or_a_reasoned_exemption()
    {
        var line = new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-001")]);
        var input = new LateSerializationStockLocation(Guid.NewGuid(), Location, "warehouse", Document,
            [line], 1, "label destroyed");

        var coverage = Assert.Single(LateSerializationCoverageRules.Validate([new(Location, "warehouse", 2)], [input]));
        Assert.Equal((2, 1, 1, "label destroyed"),
            (coverage.OpenQuantity, coverage.CommissionedQuantity, coverage.ExemptQuantity, coverage.ExemptReason));
    }

    [Fact]
    public void Missing_coverage_names_the_rule_and_unblocking_step()
    {
        var line = new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-001")]);
        var input = new LateSerializationStockLocation(Guid.NewGuid(), Location, "warehouse", Document, [line]);

        var refusal = Assert.Throws<CommandRefusedException>(() =>
            LateSerializationCoverageRules.Validate([new(Location, "warehouse", 2)], [input]));
        Assert.Equal("open_stock_not_covered", refusal.Rule);
        Assert.Contains("exempt_reason", refusal.UnblockingStep);
    }

    [Fact]
    public void Exemption_without_reason_is_refused()
    {
        var input = new LateSerializationStockLocation(Guid.NewGuid(), Location, "warehouse", Document, [], 1);
        var refusal = Assert.Throws<CommandRefusedException>(() =>
            LateSerializationCoverageRules.Validate([new(Location, "warehouse", 1)], [input]));
        Assert.Equal("exempt_reason_required", refusal.Rule);
    }

    [Fact]
    public void Exemption_adds_a_distinct_quality_approver_to_item_owner_and_inventory()
    {
        var facts = new CorrectionApprovalFacts(ExemptionRequested: true);
        var committer = new CorrectionCommitter("item-owner", [YojanaRole.ItemMasterOwner]);
        var decision = CorrectionApprovalRules.Evaluate(CorrectionApprovalRules.DefaultPolicy,
            CorrectionCommand.LateSerialization, facts, committer.Roles);
        Assert.Equal([YojanaRole.InventoryController, YojanaRole.Quality], decision.ApproverRoles);
        Assert.True(CorrectionApprovalRules.ApprovalsSatisfied(CorrectionApprovalRules.DefaultPolicy,
            CorrectionCommand.LateSerialization, facts, committer,
            [new("inventory", [YojanaRole.InventoryController]), new("quality", [YojanaRole.Quality])]));
    }
}
