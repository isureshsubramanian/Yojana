namespace Yojana.Serialisation.Contracts;

// Warranty policy model and editor API (MOD-06, P2-T03; ADR-0001 "Warranty entitlement engine", Terms). A policy is a
// series of versions effective by contract date; a version is edited only while it is a draft and is never changed once
// published (a change is a new version). Live entitlements keep the version they were created with.

/// <summary>Term length and how its end is read: months or days, the end-of-month rule, and whether the end day is covered.</summary>
public sealed record DurationRule(int Value, string Unit, string EndOfMonth = DurationRule.Clamp, bool EndInclusive = true)
{
    public const string Months = "months";
    public const string Days = "days";
    public const string Clamp = "clamp";
    public const string Roll = "roll";
}

/// <summary>One step of the start fallback chain: the trigger, plus days after it (for example invoice + 14).</summary>
public sealed record StartFallback(string Trigger, int OffsetDays = 0);

/// <summary>
/// The start trigger, the fallbacks tried in order when its evidence is missing, and the cap: the start is never later
/// than invoice + <see cref="CapDays"/>, whatever evidence arrives afterwards.
/// </summary>
public sealed record StartRule(string Trigger, IReadOnlyList<StartFallback> Fallbacks, int? CapDays);

public sealed record UsageLimitTerm(string MeterType, decimal Limit, string Period = "term");

public sealed record CoverageTerms(decimal PartsPct, decimal LabourPct, decimal TravelPct, string ServiceMode, int? ResponseHours = null, string? CoverageHours = null);

/// <summary>A refurbished-goods term keyed by (item, grade, channel, region); the statutory floor wins over a shorter term.</summary>
public sealed record RefurbishedTerm(Guid? ItemId, string Grade, string Channel, string Region, int DurationMonths, int? StatutoryFloorMonths, string StatementKind = RefurbishedWarrantyStatement.NoOemWarranty);

public static class RefurbishedWarrantyStatement
{
    public const string NoOemWarranty = "no_oem_warranty";
    public const string OemRefurbished = "oem_refurbished";
    public const string SellerRefurbished = "seller_refurbished";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([NoOemWarranty, OemRefurbished, SellerRefurbished], StringComparer.Ordinal);
}

public sealed record PolicyTerms(
    DurationRule Duration,
    string DurationBasis,
    IReadOnlyList<UsageLimitTerm> UsageLimits,
    StartRule Start,
    CoverageTerms Coverage,
    IReadOnlyList<string> Exclusions,
    IReadOnlyList<string> RegionScope,
    string Transferable,
    string SettlementFormula,
    string? ReplacementPolicyCode,
    IReadOnlyList<RefurbishedTerm> Refurbished,
    int? DoaWindowDays,
    string AssuranceOrService,
    string ReferenceTimeZoneRule,
    bool ServiceContractRegulated = false,
    EligibilityRule? Eligibility = null,
    AmendmentValueTerm? AmendmentValue = null,
    IReadOnlyList<PolicyCoverItem>? Items = null,
    int? NoClaimWithinDaysOfPurchase = null)
{
    public const string TimeZoneCustomerSite = "customer_site";
    public const string TimeZoneObligorEntity = "obligor_entity";
    public const string TimeZoneUtc = "utc";
}

/// <summary>
/// Who may buy a separately priced plan after the sale (ADR-0001 "Extended warranty"): while the unit's entitlement in
/// <see cref="BaseFamily"/> still covers it, or within <see cref="WithinDays"/> of that entitlement's start. The base
/// family is also what a <c>base_expiry</c> start follows.
/// </summary>
public sealed record EligibilityRule(string Kind, string BaseFamily, int? WithinDays = null)
{
    public const string WithinBaseCoverage = "within_base_coverage";
    public const string WithinDaysOfBaseStart = "within_days_of_base_start";
}

/// <summary>
/// What one unit of term under this policy is worth (P2-T08b), so an amendment's value is derived rather than typed:
/// <see cref="Amount"/> per day or per month changed, in <see cref="Currency"/>, which must be the tenant's currency.
/// </summary>
public sealed record AmendmentValueTerm(decimal Amount, string Currency, string Per)
{
    public const string PerDay = "day";
    public const string PerMonth = "month";
}

public sealed record PolicyIssue(string Severity, string Code, string Field, string Message)
{
    public const string Error = "error";
    public const string Warning = "warning";
}

public sealed record PolicyValidation(IReadOnlyList<PolicyIssue> Issues)
{
    public bool CanPublish => Issues.All(i => i.Severity != PolicyIssue.Error);
}

/// <summary>
/// Per-family defaults copied into a policy version when it is published. A later settings change never changes an
/// already-published version.
/// </summary>
public sealed record ClaimWaitingPeriodDefaults(IReadOnlyDictionary<string, int> DaysByCoverageFamily);

/// <summary>Opens the next version of a policy as a draft; without terms it copies the newest version's terms.</summary>
public sealed record CreatePolicyDraft(Guid CommandId, string PolicyCode, string Name, string CoverageFamily, string EntitlementType, DateOnly AppliesToContractsFrom, PolicyTerms? Terms, string ActorRef);

public sealed record UpdatePolicyDraft(Guid PolicyVersionId, string Name, DateOnly AppliesToContractsFrom, PolicyTerms Terms,
    string ActorRef, string? ExpectedVersion = null) : IExpectedDocumentVersion;

public sealed record ValidatePolicyDraft(Guid PolicyVersionId);

public sealed record PublishPolicyVersion(Guid PolicyVersionId, string ActorRef);

public sealed record DiscardPolicyDraft(Guid PolicyVersionId, string ActorRef);

public sealed record PolicyDraftResult(Guid PolicyVersionId, string PolicyCode, int Version, string Status, PolicyValidation Validation);

public sealed record DefineCoverageFamily(string FamilyCode, string Name, bool Stacking, string AccountingTag, bool Active = true,
    string ActorRef = "", string? ExpectedVersion = null) : IExpectedDocumentVersion;

public sealed record DefineReplacementPolicy(string Code, string Rule, int? MinDays, int? ExtensionMonths, string? Description,
    string ActorRef = "", string? ExpectedVersion = null) : IExpectedDocumentVersion;

public sealed record CoverageFamilyRow(string FamilyCode, string Name, bool Stacking, string AccountingTag, bool Active,
    string Version = "");

public sealed record ReplacementPolicyRow(Guid ReplacementPolicyId, string Code, string Rule, int? MinDays, int? ExtensionMonths, string? Description,
    string Version = "");

/// <summary>One policy as the editor's list shows it: the newest version and how many entitlements each version carries.</summary>
public sealed record PolicySummary(string PolicyCode, string Name, string CoverageFamily, int Versions, int? InForceVersion, DateOnly? InForceFrom, int? DraftVersion, long LiveEntitlements);

public sealed record PolicyVersionRow(Guid PolicyVersionId, int Version, DateOnly AppliesToContractsFrom, string Status, string RuleVersion, long LiveEntitlements, DateTime CreatedAt, string CreatedBy, DateTime? PublishedAt, string? PublishedBy);

public sealed record PolicyVersionDetail(Guid PolicyVersionId, string PolicyCode, int Version, string Name, string CoverageFamily, string EntitlementType, DateOnly AppliesToContractsFrom, string Status, string RuleVersion, PolicyTerms Terms, PolicyValidation Validation,
    string DocumentVersion = "", int WaitingPeriodDefaultDays = 0);

/// <summary>Where the policy stack stopped (ADR-0001 "Policy stack and resolution order").</summary>
public static class PolicyLevel
{
    public const string UnitEntitlement = "unit_entitlement";
    public const string ParentEntitlement = "parent_entitlement";
    public const string CustomerContract = "customer_contract";
    public const string ProductPolicy = "product_policy";
    public const string None = "none";

    public static readonly IReadOnlyList<string> ResolutionOrder = [UnitEntitlement, ParentEntitlement, CustomerContract, ProductPolicy];
}

/// <summary>What the stack is asked: the unit, its item and family, the contract date, the owner, and the sale's condition, channel and country.</summary>
public sealed record PolicyQuestion(Guid UnitId, Guid ItemId, string CoverageFamily, DateOnly ContractDate, Guid? OwnerPartyRef = null, string? ConditionGrade = null, string? Channel = null, string? Country = null,
    DateOnly? CoverageDate = null, string? Direction = null);

/// <summary>
/// The answer: the level that matched, the entitlement or policy version it names, and the trail of every level asked, so
/// adjudication and the exception queue can say why. <see cref="OutOfRegion"/> is set, not skipped: routing is the policy's call (case 63).
/// </summary>
public sealed record PolicyResolution(
    string Level,
    Guid? EntitlementId,
    Guid? PolicyVersionId,
    string? PolicyCode,
    int? Version,
    string? ContractRef,
    bool OutOfRegion,
    IReadOnlyList<string> Trail,
    string? EntitlementType = null,
    string? EntitlementStatus = null,
    string? Direction = null,
    CoverageTerms? Coverage = null,
    string? SettlementFormula = null,
    Guid? AccrualCohortId = null,
    Guid? ObligorEntityId = null,
    Guid? PayerPartyRef = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    int ChainLength = 0,
    DateOnly? PurchaseDate = null,
    int? NoClaimWithinDaysOfPurchase = null,
    string? EntitlementProvenance = null);
