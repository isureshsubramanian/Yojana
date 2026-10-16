using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

// MOD-06 tables (P2-T02, ADR-0001 "Core model: WarrantyEntitlement"). Composite terms (coverage, regions, usage limits,
// fallback chains, refurbished rows) are JSON objects or arrays checked with ISJSON, as elsewhere in this schema: they are
// read whole with the row and never queried by their parts.

/// <summary>Tenant reference data: a coverage family with its stacking flag and accounting tag (tickets T29, T32, T48).</summary>
public sealed class CoverageFamily : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public required string FamilyCode { get; set; }

    public required string Name { get; set; }

    /// <summary>True for add-on families that stack beside the base; the one-live-per-family rule holds either way.</summary>
    public bool Stacking { get; set; }

    public required string AccountingTag { get; set; }

    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

/// <summary>What a replacement unit inherits: the rule and its parameter (N days, or the statutory months).</summary>
public sealed class ReplacementPolicy : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid ReplacementPolicyId { get; set; }

    public required string Code { get; set; }

    public required string Rule { get; set; }

    public int? MinDays { get; set; }

    public int? ExtensionMonths { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One version of a warranty policy, effective by contract date. A published version is never edited; a change is a new
/// version (Warranty policy editor). <see cref="RuleVersion"/> is what an entitlement records as <c>computed_by_rule_version</c>.
/// </summary>
public sealed class WarrantyPolicyVersion : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid PolicyVersionId { get; set; }

    /// <summary>Groups the versions of one policy, for example <c>VX220-STD-24</c>.</summary>
    public required string PolicyCode { get; set; }

    public int Version { get; set; }

    public required string Name { get; set; }

    public required string CoverageFamily { get; set; }

    public required string EntitlementType { get; set; }

    public DateOnly AppliesToContractsFrom { get; set; }

    public required string Status { get; set; }

    public required string RuleVersion { get; set; }

    public int DurationValue { get; set; }

    /// <summary><c>months</c> or <c>days</c>.</summary>
    public required string DurationUnit { get; set; }

    /// <summary><c>clamp</c> (31 Jan + 1 month = last day of February) or <c>roll</c>.</summary>
    public required string EndOfMonthRule { get; set; }

    public bool EndInclusive { get; set; } = true;

    public required string DurationBasis { get; set; }

    /// <summary>JSON array of {meter_type, limit, period}.</summary>
    public string UsageLimits { get; set; } = "[]";

    public required string StartTrigger { get; set; }

    /// <summary>JSON array of fallbacks in order, each {trigger, offset_days}.</summary>
    public string FallbackChain { get; set; } = "[]";

    /// <summary>The start can never be later than the invoice plus this many days, whatever evidence arrives later.</summary>
    public int? StartCapDays { get; set; }

    /// <summary>JSON object: parts_pct, labour_pct, travel_pct, service_mode, response_hours, coverage_hours.</summary>
    public string Coverage { get; set; } = "{}";

    public string Exclusions { get; set; } = "[]";

    public string RegionScope { get; set; } = "[]";

    public required string Transferable { get; set; }

    public required string SettlementFormula { get; set; }

    public Guid? ReplacementPolicyId { get; set; }

    /// <summary>JSON array of {item_id, grade, channel, region, duration_months, statutory_floor_months}.</summary>
    public string RefurbishedPolicies { get; set; } = "[]";

    public int? DoaWindowDays { get; set; }

    public required string AssuranceOrService { get; set; }

    /// <summary><c>customer_site</c>, <c>obligor_entity</c> or <c>utc</c>: whose calendar day starts and ends the term (case 112).</summary>
    public required string ReferenceTimeZoneRule { get; set; }

    /// <summary>The flag for separately priced plans under service-contract regulation (OD-05).</summary>
    public bool ServiceContractRegulated { get; set; }

    /// <summary>JSON <see cref="Contracts.EligibilityRule"/>, null when anyone may buy the plan (P2-T07).</summary>
    public string? Eligibility { get; set; }

    /// <summary>JSON <see cref="Contracts.AmendmentValueTerm"/>: what a day or month of this term is worth, null when the policy states no value (P2-T08b).</summary>
    public string? AmendmentValue { get; set; }

    public DateTime CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime? PublishedAt { get; set; }

    public string? PublishedBy { get; set; }
}

/// <summary>The accrual cohort an entitlement's reserve belongs to (obligor, item, period, tag); inherited entitlements carry none (case 71).</summary>
public sealed class AccrualCohort : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid CohortId { get; set; }

    public Guid ObligorEntityId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>yyyy-MM of the sale.</summary>
    public required string Period { get; set; }

    public required string AssuranceOrService { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A customer-owned coverage attached to one unit at a time. At most one live entitlement per (unit, coverage family);
/// a closed entitlement is never reopened and <see cref="EndDate"/> is never recomputed in place (MOD-06 invariants 1, 2, 5).
/// </summary>
public sealed class Entitlement : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid EntitlementId { get; set; }

    public Guid UnitId { get; set; }

    public required string Scope { get; set; }

    public Guid? ScopeChildUnitId { get; set; }

    public string? ScopeItemClass { get; set; }

    public required string Type { get; set; }

    public required string CoverageFamily { get; set; }

    public required string Direction { get; set; }

    public Guid? OwnerPartyRef { get; set; }

    public string? OwnerPartyType { get; set; }

    public Guid ObligorEntityId { get; set; }

    public Guid? PayerPartyRef { get; set; }

    public string? PayerPartyType { get; set; }

    public Guid PolicyVersionId { get; set; }

    public required string StartTrigger { get; set; }

    /// <summary>Which trigger of the fallback chain actually fired.</summary>
    public string? StartBasis { get; set; }

    public string? StartEvidenceRef { get; set; }

    /// <summary>Null while pending evidence.</summary>
    public DateOnly? StartDate { get; set; }

    /// <summary>The IANA zone the start and end days are read in (case 112).</summary>
    public required string StartTimeZone { get; set; }

    public DateOnly? EndDate { get; set; }

    public string? ComputedByRuleVersion { get; set; }

    /// <summary>The cap's ceiling: the start can never move later than this day, whatever evidence arrives (invoice + cap days).</summary>
    public DateOnly? StartLatest { get; set; }

    // The term actually applied (the policy's, or a refurbished row's with the floor): a rebase recomputes the end from
    // these, never from a policy that may have been read differently, so the end date stays the rule's output (invariant 5).
    public int DurationValue { get; set; }

    public required string DurationUnit { get; set; }

    public required string EndOfMonthRule { get; set; }

    public bool EndInclusive { get; set; } = true;

    /// <summary>
    /// Days the end stands beyond the term counted from the start: paused suspension days, an amended end, a replacement
    /// rule's extension. A rebase recomputes the term from the new start and adds this, so none of them is lost.
    /// </summary>
    public int EndAdjustmentDays { get; set; }

    public required string DurationBasis { get; set; }

    public string Coverage { get; set; } = "{}";

    public string RegionScope { get; set; } = "[]";

    public required string Transferable { get; set; }

    public string? ConditionAtCreation { get; set; }

    public Guid? ParentEntitlementId { get; set; }

    public Guid? RootEntitlementId { get; set; }

    public Guid? ReplacementLinkId { get; set; }

    public Guid? AccrualCohortId { get; set; }

    public string? RevenueScheduleRef { get; set; }

    public required string AssuranceOrService { get; set; }

    public required string Status { get; set; }

    public string? ClosedReason { get; set; }

    /// <summary>The business date the entitlement stopped covering (the effective date of a supersession, the return date).</summary>
    public DateOnly? ClosedEffective { get; set; }

    public DateTime? ClosedAt { get; set; }

    public Guid? SuccessorUnitId { get; set; }

    public Guid? SuccessorEntitlementId { get; set; }

    public Guid? SupersedesEntitlementId { get; set; }

    public required string Confidence { get; set; }

    public bool StatutoryExtensionApplied { get; set; }

    public required string Provenance { get; set; }

    public string? SourceDocumentType { get; set; }

    public Guid? SourceDocumentId { get; set; }

    public int? SourceDocumentLine { get; set; }

    public string? CustomerPoRef { get; set; }

    public Guid CreatedEventId { get; set; }

    public DateTime CreatedAt { get; set; }
}

public sealed class EntitlementSuspension : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid SuspensionId { get; set; }

    public Guid EntitlementId { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public required string Reason { get; set; }

    /// <summary>When true, resuming shifts the end date by the paused interval (case 62).</summary>
    public bool ClockPaused { get; set; }

    public required string ActorRef { get; set; }

    public Guid EventId { get; set; }

    public Guid? ResumedEventId { get; set; }
}

public sealed class EntitlementRebase : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid RebaseId { get; set; }

    public Guid EntitlementId { get; set; }

    public DateOnly? OldStart { get; set; }

    public DateOnly NewStart { get; set; }

    public DateOnly? OldEnd { get; set; }

    public DateOnly NewEnd { get; set; }

    public string? EvidenceRef { get; set; }

    public bool PolicyCapApplied { get; set; }

    public required string ActorRef { get; set; }

    public DateTime At { get; set; }

    public Guid EventId { get; set; }
}

/// <summary>One field changed by AmendEntitlement, before and after, with the approval that allowed it.</summary>
public sealed class EntitlementAmendment : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid AmendmentId { get; set; }

    public Guid EntitlementId { get; set; }

    public required string Field { get; set; }

    public string? BeforeValue { get; set; }

    public string? AfterValue { get; set; }

    public required string ReasonCode { get; set; }

    public string? SourceDocumentType { get; set; }

    public Guid? SourceDocumentId { get; set; }

    public int? DaysChanged { get; set; }

    public string? ApprovedBy { get; set; }

    public string? CustomerNotificationRef { get; set; }

    public required string ActorRef { get; set; }

    public DateTime At { get; set; }

    public Guid EventId { get; set; }
}

public sealed class EntitlementUsageLimit : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid EntitlementId { get; set; }

    public required string MeterType { get; set; }

    public decimal LimitValue { get; set; }

    public decimal UsedValue { get; set; }

    /// <summary><c>term</c> or <c>contract_year</c>.</summary>
    public required string Period { get; set; }

    public DateOnly? ResetsOn { get; set; }
}
