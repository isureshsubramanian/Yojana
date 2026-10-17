namespace Yojana.Serialisation.Contracts;

/// <summary>Facts the command has established about every affected unit and document before approval is evaluated.</summary>
public sealed record CorrectionApprovalFacts(
    bool UnitHasShipped = false,
    bool UnitAppearsOnExternalDocument = false,
    bool OwningPartyChanges = false,
    bool DocumentInvoiced = false,
    bool UnitSold = false,
    bool UnitHasLiveEntitlement = false,
    bool ExemptionRequested = false);

/// <summary>A condition stored in the tenant's correction policy.</summary>
public sealed record CorrectionApprovalCondition(string Fact, string Action, IReadOnlyList<string> Roles, string Rule, string UnblockingStep)
{
    public const string NeedsApprover = "needs_approver";
    public const string Refused = "refused";
    public const string ReviewTask = "review_task";
}

/// <summary>Approval rule for one correction command. Any commit role may commit; every required role is additive.</summary>
public sealed record CorrectionCommandApprovalRule(
    string Command,
    IReadOnlyList<string> CommitRoles,
    IReadOnlyList<string>? RequiredRoles = null,
    IReadOnlyList<string>? SoldCommitRoles = null,
    IReadOnlyList<CorrectionApprovalCondition>? Conditions = null);

/// <summary>Tenant override of who may initiate and commit one workbench choice.</summary>
public sealed record CorrectionWorkbenchApprovalRule(string Choice, IReadOnlyList<string> Commands, IReadOnlyList<string> CommitRoles);

public sealed record CorrectionApprovalPolicy(
    IReadOnlyList<CorrectionCommandApprovalRule> Commands,
    IReadOnlyList<CorrectionWorkbenchApprovalRule> Workbench,
    IReadOnlyList<string> WorkbenchInitiatorRoles);

public static class CorrectionApprovalStatus
{
    public const string Allowed = "allowed";
    public const string NeedsApproval = "needs_approval";
    public const string Refused = "refused";
}

/// <summary>Pure approval result; review tasks are obligations, not approver roles.</summary>
public sealed record CorrectionApprovalDecision(
    string Status,
    IReadOnlyList<string> CommitRoles,
    IReadOnlyList<string> ApproverRoles,
    IReadOnlyList<string> ReviewTasks,
    CommandRefusal? Refusal = null);

/// <summary>OD-07 tenant data: default serial re-use rule with optional item-class overrides.</summary>
public sealed record SerialReusePolicy(string Default, IReadOnlyList<SerialReuseItemClassOverride> ItemClasses);

public sealed record SerialReuseItemClassOverride(string ItemClass, string Policy);

public static class SerialReusePolicyValue
{
    public const string Never = "never";
    public const string NeverShippedAndNotExternal = "never_shipped_and_not_external";
    public static readonly IReadOnlyList<string> All = [Never, NeverShippedAndNotExternal];
}

public static class CorrectionApprovalFact
{
    public const string Always = "always";
    public const string Shipped = "unit_shipped";
    public const string ExternalDocument = "external_document";
    public const string OwningPartyChanges = "owning_party_changes";
    public const string DocumentInvoiced = "document_invoiced";
    public const string LiveEntitlement = "live_entitlement";
    public const string ExemptionRequested = "exemption_requested";
    public const string Sold = "unit_sold";
}

public static class CorrectionWorkbenchChoice
{
    public const string WrongDocumentSerials = "wrong_document_serials";
    public const string ReplacedOrExchanged = "replaced_or_exchanged";
    public const string RefurbishedAndReserialised = "refurbished_and_reserialised";
    public const string MistypedOrCrossed = "mistyped_or_crossed";
    public const string WrongItem = "wrong_item";
    public const string WrongOrder = "wrong_order";
    public const string WrongAllocation = "wrong_allocation";
    public const string DuplicateUnit = "duplicate_unit";
    public const string WrongComponents = "wrong_components";
    public const string WrongWarranty = "wrong_warranty";

    public static readonly IReadOnlyList<string> All =
    [
        WrongDocumentSerials, ReplacedOrExchanged, RefurbishedAndReserialised, MistypedOrCrossed, WrongItem,
        WrongOrder, WrongAllocation, DuplicateUnit, WrongComponents, WrongWarranty,
    ];
}
