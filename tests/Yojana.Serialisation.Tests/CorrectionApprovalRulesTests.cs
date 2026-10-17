using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Tests;

/// <summary>P3-T02: every ADR correction-approval row and its conditional escalation.</summary>
public sealed class CorrectionApprovalRulesTests
{
    private static readonly CorrectionApprovalPolicy Policy = CorrectionApprovalRules.DefaultPolicy;

    [Theory]
    [InlineData(CorrectionCommand.CorrectIdentifier, YojanaRole.SerialAdministrator)]
    [InlineData(CorrectionCommand.SwapIdentifiers, YojanaRole.SerialAdministrator)]
    [InlineData(CorrectionCommand.CorrectItem, YojanaRole.InventoryController)]
    [InlineData(CorrectionCommand.CorrectAsBuilt, YojanaRole.ProductionSupervisor)]
    [InlineData(CorrectionCommand.VoidUnit, YojanaRole.ProductionSupervisor)]
    [InlineData(CorrectionCommand.AssignIdentifiers, YojanaRole.ProductionSupervisor)]
    [InlineData(CorrectionCommand.ReassignAllocation, YojanaRole.ProductionSupervisor)]
    [InlineData(CorrectionCommand.CorrectSourceDocument, YojanaRole.OrderDeskSupervisor)]
    public void Default_single_role_rows_allow_the_tabled_role(string command, string role)
    {
        Assert.Equal(CorrectionApprovalStatus.Allowed, CorrectionApprovalRules.Evaluate(Policy, command, new(), [role]).Status);
    }

    [Fact]
    public void Late_serialisation_and_document_correction_require_their_second_tabled_role()
    {
        var late = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.LateSerialization, new(), [YojanaRole.ItemMasterOwner]);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, late.Status);
        Assert.Equal([YojanaRole.InventoryController], late.ApproverRoles);

        var document = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.CorrectDocumentSerials, new(), [YojanaRole.ShippingSupervisor]);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, document.Status);
        Assert.Equal([YojanaRole.ServiceSupervisor], document.ApproverRoles);
    }

    [Fact]
    public void Conditional_facts_add_the_second_approver_or_finance_task_and_void_is_refused()
    {
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.SwapIdentifiers, new(UnitHasShipped: true), [YojanaRole.SerialAdministrator]).Status);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.CorrectIdentifier, new(UnitAppearsOnExternalDocument: true), [YojanaRole.SerialAdministrator]).Status);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.CorrectAsBuilt, new(UnitHasShipped: true), [YojanaRole.Quality]).Status);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.AssignIdentifiers, new(UnitHasShipped: true), [YojanaRole.ProductionSupervisor]).Status);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.CorrectSourceDocument, new(OwningPartyChanges: true), [YojanaRole.OrderDeskSupervisor]).Status);

        var sold = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.CorrectItem, new(UnitSold: true), [YojanaRole.ServiceSupervisor]);
        Assert.Equal(CorrectionApprovalStatus.Allowed, sold.Status);
        Assert.Equal([YojanaRole.FinanceReview], sold.ReviewTasks);
        var invoiced = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.CorrectDocumentSerials, new(DocumentInvoiced: true), [YojanaRole.ShippingSupervisor]);
        Assert.Equal([YojanaRole.FinanceReview], invoiced.ReviewTasks);

        var shippedVoid = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.VoidUnit, new(UnitHasShipped: true), [YojanaRole.ProductionSupervisor]);
        Assert.Equal(CorrectionApprovalStatus.Refused, shippedVoid.Status);
        Assert.Equal("unit_ever_shipped", shippedVoid.Refusal!.Rule);
        Assert.NotNull(shippedVoid.Refusal.UnblockingStep);
    }

    [Fact]
    public void A_shipped_swap_needs_two_distinct_people_from_the_tenant_policy()
    {
        var facts = new CorrectionApprovalFacts(UnitHasShipped: true);
        var committer = new CorrectionCommitter("first", [YojanaRole.SerialAdministrator]);
        Assert.False(CorrectionApprovalRules.ApprovalsSatisfied(Policy, CorrectionCommand.SwapIdentifiers, facts, committer, []));
        Assert.False(CorrectionApprovalRules.ApprovalsSatisfied(Policy, CorrectionCommand.SwapIdentifiers, facts, committer,
            [new("first", [YojanaRole.SerialAdministrator])]));
        Assert.True(CorrectionApprovalRules.ApprovalsSatisfied(Policy, CorrectionCommand.SwapIdentifiers, facts, committer,
            [new("second", [YojanaRole.SerialAdministrator])]));
    }

    [Fact]
    public void Merge_always_needs_a_second_approver_and_live_cover_adds_the_service_manager()
    {
        var ordinary = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.MergeUnits, new(), [YojanaRole.SerialAdministrator]);
        Assert.Equal([YojanaRole.SerialAdministrator], ordinary.ApproverRoles);
        var covered = CorrectionApprovalRules.Evaluate(Policy, CorrectionCommand.MergeUnits, new(UnitHasLiveEntitlement: true), [YojanaRole.SerialAdministrator]);
        Assert.Equal([YojanaRole.SerialAdministrator, YojanaRole.ServiceManager], covered.ApproverRoles);
    }

    [Fact]
    public void Technicians_initiate_but_do_not_commit_by_default()
    {
        Assert.Contains(YojanaRole.Technician, Policy.WorkbenchInitiatorRoles);
        Assert.All(Policy.Commands, rule => Assert.Equal(CorrectionApprovalStatus.NeedsApproval,
            CorrectionApprovalRules.Evaluate(Policy, rule.Command, new(), [YojanaRole.Technician]).Status));
    }

    [Fact]
    public void The_default_policy_is_complete_and_valid()
    {
        Assert.Empty(CorrectionApprovalRules.Validate(Policy));
        Assert.Equal(CorrectionCommand.All.Order(), Policy.Commands.Select(r => r.Command).Order());
        Assert.Equal(10, Policy.Workbench.Count);
    }

    [Fact]
    public void Workbench_choices_have_their_own_commit_roles_and_technicians_route_to_them()
    {
        Assert.Equal(CorrectionApprovalStatus.Allowed,
            CorrectionApprovalRules.EvaluateWorkbenchChoice(Policy, CorrectionWorkbenchChoice.WrongAllocation, [YojanaRole.ProductionSupervisor]).Status);
        var technician = CorrectionApprovalRules.EvaluateWorkbenchChoice(Policy, CorrectionWorkbenchChoice.WrongAllocation, [YojanaRole.Technician]);
        Assert.Equal(CorrectionApprovalStatus.NeedsApproval, technician.Status);
        Assert.Equal("approval_required", technician.Refusal!.Rule);
    }

    [Fact]
    public void Serial_reuse_defaults_to_never_and_an_item_class_can_opt_in()
    {
        Assert.Equal(SerialReusePolicyValue.Never, SerialReuseSettings.Resolve(SerialReuseSettings.DefaultPolicy, "server"));
        var policy = new SerialReusePolicy(SerialReusePolicyValue.Never,
            [new("non_regulated_accessory", SerialReusePolicyValue.NeverShippedAndNotExternal)]);
        Assert.Equal(SerialReusePolicyValue.NeverShippedAndNotExternal, SerialReuseSettings.Resolve(policy, "non_regulated_accessory"));
        Assert.Equal(SerialReusePolicyValue.Never, SerialReuseSettings.Resolve(policy, "server"));
    }
}
