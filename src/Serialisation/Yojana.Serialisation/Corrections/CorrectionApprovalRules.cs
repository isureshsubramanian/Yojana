using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Corrections;

/// <summary>Pure correction approval rules over the tenant's policy and facts established by the command.</summary>
public static class CorrectionApprovalRules
{
    private static CorrectionApprovalCondition Approver(string fact, IReadOnlyList<string> roles) =>
        new(fact, CorrectionApprovalCondition.NeedsApprover, roles, "approval_required", $"route the identical preview to {string.Join(" or ", roles)}");

    public static readonly CorrectionApprovalPolicy DefaultPolicy = new(
    [
        new(CorrectionCommand.CorrectIdentifier, [YojanaRole.SerialAdministrator], Conditions:
        [
            Approver(CorrectionApprovalFact.Shipped, [YojanaRole.SerialAdministrator]),
            Approver(CorrectionApprovalFact.ExternalDocument, [YojanaRole.SerialAdministrator]),
        ]),
        new(CorrectionCommand.SwapIdentifiers, [YojanaRole.SerialAdministrator], Conditions:
        [Approver(CorrectionApprovalFact.Shipped, [YojanaRole.SerialAdministrator])]),
        new(CorrectionCommand.CorrectItem, [YojanaRole.InventoryController], SoldCommitRoles: [YojanaRole.ServiceSupervisor], Conditions:
        [new(CorrectionApprovalFact.Sold, CorrectionApprovalCondition.ReviewTask, [YojanaRole.FinanceReview], "finance_review_required", "raise a finance review task")]),
        new(CorrectionCommand.CorrectAsBuilt, [YojanaRole.ProductionSupervisor, YojanaRole.Quality], Conditions:
        [Approver(CorrectionApprovalFact.Shipped, [YojanaRole.ProductionSupervisor, YojanaRole.Quality])]),
        new(CorrectionCommand.LateSerialization, [YojanaRole.ItemMasterOwner], RequiredRoles: [YojanaRole.InventoryController], Conditions:
        [Approver(CorrectionApprovalFact.ExemptionRequested, [YojanaRole.Quality])]),
        new(CorrectionCommand.VoidUnit, [YojanaRole.ProductionSupervisor], Conditions:
        [
            new(CorrectionApprovalFact.Shipped, CorrectionApprovalCondition.Refused, [], "unit_ever_shipped", "a shipped unit is not voided; use the correction matching the physical outcome"),
            new(CorrectionApprovalFact.ExternalDocument, CorrectionApprovalCondition.Refused, [], "unit_on_external_document", "correct the external document instead of voiding the unit"),
        ]),
        new(CorrectionCommand.MergeUnits, [YojanaRole.SerialAdministrator], Conditions:
        [
            Approver(CorrectionApprovalFact.Always, [YojanaRole.SerialAdministrator]),
            Approver(CorrectionApprovalFact.LiveEntitlement, [YojanaRole.ServiceManager]),
        ]),
        new(CorrectionCommand.AssignIdentifiers, [YojanaRole.ProductionSupervisor], Conditions:
        [Approver(CorrectionApprovalFact.Shipped, [YojanaRole.ProductionSupervisor])]),
        new(CorrectionCommand.ReassignAllocation, [YojanaRole.ProductionSupervisor]),
        new(CorrectionCommand.CorrectDocumentSerials, [YojanaRole.ShippingSupervisor], RequiredRoles: [YojanaRole.ServiceSupervisor], Conditions:
        [new(CorrectionApprovalFact.DocumentInvoiced, CorrectionApprovalCondition.ReviewTask, [YojanaRole.FinanceReview], "finance_review_required", "raise a finance review task for the invoiced document")]),
        new(CorrectionCommand.CorrectSourceDocument, [YojanaRole.OrderDeskSupervisor], Conditions:
        [Approver(CorrectionApprovalFact.OwningPartyChanges, [YojanaRole.ServiceManager])]),
    ],
    [
        new(CorrectionWorkbenchChoice.WrongDocumentSerials, [CorrectionCommand.CorrectDocumentSerials], [YojanaRole.ShippingSupervisor]),
        new(CorrectionWorkbenchChoice.ReplacedOrExchanged, [], [YojanaRole.ServiceSupervisor]),
        new(CorrectionWorkbenchChoice.RefurbishedAndReserialised, [CorrectionCommand.CorrectIdentifier], [YojanaRole.ProductionSupervisor, YojanaRole.ServiceSupervisor]),
        new(CorrectionWorkbenchChoice.MistypedOrCrossed, [CorrectionCommand.CorrectIdentifier, CorrectionCommand.SwapIdentifiers], [YojanaRole.SerialAdministrator]),
        new(CorrectionWorkbenchChoice.WrongItem, [CorrectionCommand.CorrectItem], [YojanaRole.InventoryController, YojanaRole.ServiceSupervisor]),
        new(CorrectionWorkbenchChoice.WrongOrder, [CorrectionCommand.CorrectSourceDocument], [YojanaRole.OrderDeskSupervisor]),
        new(CorrectionWorkbenchChoice.WrongAllocation, [CorrectionCommand.ReassignAllocation, CorrectionCommand.VoidUnit], [YojanaRole.ProductionSupervisor]),
        new(CorrectionWorkbenchChoice.DuplicateUnit, [CorrectionCommand.MergeUnits], [YojanaRole.SerialAdministrator]),
        new(CorrectionWorkbenchChoice.WrongComponents, [CorrectionCommand.CorrectAsBuilt, CorrectionCommand.LateSerialization, CorrectionCommand.AssignIdentifiers], [YojanaRole.ProductionSupervisor, YojanaRole.Quality, YojanaRole.ItemMasterOwner]),
        new(CorrectionWorkbenchChoice.WrongWarranty, [], [YojanaRole.ServiceDesk, YojanaRole.ServiceManager]),
    ],
    [YojanaRole.CustomerService, YojanaRole.WarehouseLead, YojanaRole.ProductionSupervisor, YojanaRole.Technician]);

    public static CorrectionApprovalDecision Evaluate(CorrectionApprovalPolicy policy, string command, CorrectionApprovalFacts facts, IReadOnlyList<string> committerRoles)
    {
        var rule = policy.Commands.FirstOrDefault(r => r.Command == command);
        if (rule is null)
        {
            var refusal = new CommandRefusal("approval_policy_missing", $"Correction command '{command}' has no tenant approval rule.", "add the command to the tenant correction approval policy");
            return new(CorrectionApprovalStatus.Refused, [], [], [], refusal);
        }

        var commitRoles = facts.UnitSold && rule.SoldCommitRoles is { Count: > 0 } sold ? sold : rule.CommitRoles;
        var approvers = new List<string>();
        if (!committerRoles.Intersect(commitRoles, StringComparer.Ordinal).Any()) approvers.AddRange(commitRoles);
        if (rule.RequiredRoles is { Count: > 0 }) approvers.AddRange(rule.RequiredRoles);
        var tasks = new List<string>();

        foreach (var condition in rule.Conditions ?? [])
        {
            if (!Applies(condition.Fact, facts)) continue;
            if (condition.Action == CorrectionApprovalCondition.Refused)
            {
                return new(CorrectionApprovalStatus.Refused, commitRoles, [], tasks, new(condition.Rule, $"The correction is refused by '{condition.Rule}'.", condition.UnblockingStep));
            }

            if (condition.Action == CorrectionApprovalCondition.NeedsApprover) approvers.AddRange(condition.Roles);
            if (condition.Action == CorrectionApprovalCondition.ReviewTask) tasks.AddRange(condition.Roles);
        }

        approvers = approvers.Distinct(StringComparer.Ordinal).ToList();
        tasks = tasks.Distinct(StringComparer.Ordinal).ToList();
        if (approvers.Count == 0) return new(CorrectionApprovalStatus.Allowed, commitRoles, approvers, tasks);
        var unblock = $"route the identical preview to {string.Join(" or ", approvers)}";
        return new(CorrectionApprovalStatus.NeedsApproval, commitRoles, approvers, tasks,
            new("approval_required", $"'{command}' needs approval by {string.Join(" or ", approvers)}.", unblock));
    }

    public static CorrectionApprovalDecision EvaluateWorkbenchChoice(CorrectionApprovalPolicy policy, string choice, IReadOnlyList<string> committerRoles)
    {
        var rule = policy.Workbench.FirstOrDefault(r => r.Choice == choice);
        if (rule is null)
        {
            var refusal = new CommandRefusal("workbench_approval_policy_missing", $"Workbench choice '{choice}' has no tenant approval rule.", "add the choice to the tenant correction approval policy");
            return new(CorrectionApprovalStatus.Refused, [], [], [], refusal);
        }

        if (committerRoles.Intersect(rule.CommitRoles, StringComparer.Ordinal).Any()) return new(CorrectionApprovalStatus.Allowed, rule.CommitRoles, [], []);
        return new(CorrectionApprovalStatus.NeedsApproval, rule.CommitRoles, rule.CommitRoles, [],
            new("approval_required", $"Workbench choice '{choice}' needs an authorised committer.", $"route the identical preview to {string.Join(" or ", rule.CommitRoles)}"));
    }

    public static IReadOnlyList<string> Validate(CorrectionApprovalPolicy policy)
    {
        var problems = new List<string>();
        foreach (var command in CorrectionCommand.All.Except(policy.Commands.Select(r => r.Command))) problems.Add($"command '{command}' has no rule");
        foreach (var rule in policy.Commands)
        {
            if (!CorrectionCommand.All.Contains(rule.Command)) problems.Add($"'{rule.Command}' is not a correction command");
            if (policy.Commands.Count(r => r.Command == rule.Command) > 1) problems.Add($"command '{rule.Command}' has more than one rule");
            if (rule.CommitRoles.Count == 0) problems.Add($"command '{rule.Command}' has no commit role");
            foreach (var role in rule.CommitRoles.Concat(rule.RequiredRoles ?? []).Concat(rule.SoldCommitRoles ?? []).Concat((rule.Conditions ?? []).SelectMany(c => c.Roles)).Where(r => !YojanaRole.IsKnown(r)))
                problems.Add($"command '{rule.Command}': '{role}' is not a role");
            foreach (var condition in rule.Conditions ?? [])
            {
                if (!new[] { CorrectionApprovalFact.Always, CorrectionApprovalFact.Shipped, CorrectionApprovalFact.ExternalDocument, CorrectionApprovalFact.OwningPartyChanges, CorrectionApprovalFact.DocumentInvoiced, CorrectionApprovalFact.LiveEntitlement, CorrectionApprovalFact.ExemptionRequested, CorrectionApprovalFact.Sold }.Contains(condition.Fact))
                    problems.Add($"command '{rule.Command}': '{condition.Fact}' is not an approval fact");
                if (!new[] { CorrectionApprovalCondition.NeedsApprover, CorrectionApprovalCondition.Refused, CorrectionApprovalCondition.ReviewTask }.Contains(condition.Action))
                    problems.Add($"command '{rule.Command}': '{condition.Action}' is not an approval action");
            }
        }

        foreach (var role in policy.WorkbenchInitiatorRoles.Where(r => !YojanaRole.IsKnown(r))) problems.Add($"workbench initiator '{role}' is not a role");
        foreach (var choice in CorrectionWorkbenchChoice.All.Except(policy.Workbench.Select(r => r.Choice))) problems.Add($"workbench choice '{choice}' has no rule");
        foreach (var rule in policy.Workbench)
        {
            if (!CorrectionWorkbenchChoice.All.Contains(rule.Choice)) problems.Add($"'{rule.Choice}' is not a workbench choice");
            if (policy.Workbench.Count(r => r.Choice == rule.Choice) > 1) problems.Add($"workbench choice '{rule.Choice}' has more than one rule");
            if (rule.CommitRoles.Count == 0) problems.Add($"workbench choice '{rule.Choice}' has no commit role");
            foreach (var role in rule.CommitRoles.Where(r => !YojanaRole.IsKnown(r))) problems.Add($"workbench choice '{rule.Choice}': '{role}' is not a role");
        }
        return problems.Distinct().ToList();
    }

    private static bool Applies(string fact, CorrectionApprovalFacts facts) => fact switch
    {
        CorrectionApprovalFact.Always => true,
        CorrectionApprovalFact.Shipped => facts.UnitHasShipped,
        CorrectionApprovalFact.ExternalDocument => facts.UnitAppearsOnExternalDocument,
        CorrectionApprovalFact.OwningPartyChanges => facts.OwningPartyChanges,
        CorrectionApprovalFact.DocumentInvoiced => facts.DocumentInvoiced,
        CorrectionApprovalFact.LiveEntitlement => facts.UnitHasLiveEntitlement,
        CorrectionApprovalFact.ExemptionRequested => facts.ExemptionRequested,
        CorrectionApprovalFact.Sold => facts.UnitSold,
        _ => false,
    };
}

/// <summary>OD-07 serial re-use setting; missing data means never re-use, and item-class exceptions are explicit.</summary>
public sealed class SerialReuseSettings(ITenantSettings settings)
{
    public const string Key = "corrections.serial_reuse";
    public static readonly SerialReusePolicy DefaultPolicy = new(SerialReusePolicyValue.Never, []);

    // Coupled to the factory registration in SerialisationModule for the same two-DbContext Wolverine constraint as
    // CorrectionApprovalSettings; VoidUnit and commissioning consume this wrapper, never ITenantSettings directly.
    public async Task<SerialReusePolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<SerialReusePolicy>(text, LedgerJson.Options) ?? DefaultPolicy
            : DefaultPolicy;

    public static string Resolve(SerialReusePolicy policy, string? itemClass) =>
        itemClass is not null && policy.ItemClasses.FirstOrDefault(i => i.ItemClass == itemClass) is { } item ? item.Policy : policy.Default;

    public async Task SetAsync(SerialReusePolicy policy, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (!SerialReusePolicyValue.All.Contains(policy.Default)) problems.Add($"default '{policy.Default}' is not a serial re-use policy");
        if (policy.ItemClasses.Any(i => string.IsNullOrWhiteSpace(i.ItemClass))) problems.Add("an item-class override has no item class");
        if (policy.ItemClasses.GroupBy(i => i.ItemClass, StringComparer.Ordinal).Any(g => g.Count() > 1)) problems.Add("an item class has more than one serial re-use override");
        foreach (var value in policy.ItemClasses.Where(i => !SerialReusePolicyValue.All.Contains(i.Policy))) problems.Add($"item class '{value.ItemClass}' has unknown policy '{value.Policy}'");
        if (problems.Count > 0) throw new CommandRefusedException("serial_reuse_policy_invalid", string.Join("; ", problems));
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }
}

/// <summary>The tenant's correction approval policy, stored as data with the ADR table as its missing-setting default.</summary>
public sealed class CorrectionApprovalSettings(ITenantSettings settings)
{
    public const string Key = "corrections.approval";

    // Coupled to the factory registration in SerialisationModule: ITenantSettings owns PlatformDbContext, which must stay
    // outside Wolverine correction-handler dependency graphs or host startup sees two DbContext types and refuses them.
    public async Task<CorrectionApprovalPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<CorrectionApprovalPolicy>(text, LedgerJson.Options) ?? CorrectionApprovalRules.DefaultPolicy
            : CorrectionApprovalRules.DefaultPolicy;

    public async Task SetAsync(CorrectionApprovalPolicy policy, CancellationToken cancellationToken)
    {
        if (CorrectionApprovalRules.Validate(policy) is { Count: > 0 } problems) throw new CommandRefusedException("approval_policy_invalid", string.Join("; ", problems));
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }
}
