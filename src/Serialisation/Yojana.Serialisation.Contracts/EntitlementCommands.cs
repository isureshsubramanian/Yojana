namespace Yojana.Serialisation.Contracts;

// Entitlement commands of P2-T05 (ADR-0001 "Core model: WarrantyEntitlement", state machine). Each is a fact on the
// unit's ledger with a deterministic idempotency key from the command id; a replay returns the same result. Times are
// UTC instants; the calendar days they fall on are read in the entitlement's reference time zone.

/// <summary>
/// Creates an entitlement from a resolved policy (P2-T04 stack, or a named version). Evidence maps start triggers to the
/// instants they happened; with none of the chain's triggers present the entitlement is created pending evidence.
/// </summary>
public sealed record CreateEntitlement(
    Guid CommandId,
    Guid UnitId,
    string CoverageFamily,
    DateOnly ContractDate,
    Guid ObligorEntityId,
    DateTime EventTime,
    string ActorRef,
    IReadOnlyDictionary<string, DateTime> Evidence,
    Guid? PolicyVersionId = null,
    Guid? OwnerPartyRef = null,
    string? OwnerPartyType = null,
    Guid? PayerPartyRef = null,
    string? PayerPartyType = null,
    string? ConditionGrade = null,
    string? Channel = null,
    string? Country = null,
    Guid? SiteLocationId = null,
    string? StartEvidenceRef = null,
    DocumentRef? SourceDocument = null,
    string? CustomerPoRef = null,
    string Provenance = EntitlementProvenance.Created,
    string Confidence = "high");

public sealed record CreateEntitlementResult(Guid EntitlementId, string Status, string PolicyLevel, string? PolicyCode, int? PolicyVersion, DateOnly? StartDate, DateOnly? EndDate, string? StartBasis, bool CapApplied, bool FloorApplied, string TimeZone, Guid? AccrualCohortId, IReadOnlyList<string> Trail);

/// <summary>New start evidence: attaches the first evidence to a pending entitlement, or moves an active one's start (held to the cap).</summary>
public sealed record RebaseEntitlementStart(Guid CommandId, Guid EntitlementId, string Trigger, DateTime EvidenceAt, string? EvidenceRef, DateTime EventTime, string ActorRef);

public sealed record SuspendEntitlement(Guid CommandId, Guid EntitlementId, DateOnly From, string Reason, bool ClockPaused, DateTime EventTime, string ActorRef);

public sealed record ResumeEntitlement(Guid CommandId, Guid EntitlementId, DateOnly On, DateTime EventTime, string ActorRef);

/// <summary>Void is per entitlement with a reason and evidence (case 61); a damage finding is a claim denial, never this.</summary>
public sealed record VoidEntitlement(Guid CommandId, Guid EntitlementId, string ClosedReason, string Explanation, IReadOnlyList<string> EvidenceRefs, DateOnly Effective, DateTime EventTime, string ActorRef);

public sealed record CloseEntitlement(Guid CommandId, Guid EntitlementId, string ClosedReason, DateOnly Effective, DateTime EventTime, string ActorRef, DocumentRef? SourceDocument = null, string? Note = null);

public sealed record EntitlementChangeResult(Guid EntitlementId, string Status, string? ClosedReason, DateOnly? StartDate, DateOnly? EndDate, Guid EventId, bool Replayed);

/// <summary>Moves a live entitlement to a replacement unit under a named rule, or the source policy's rule when omitted.</summary>
public sealed record TransferEntitlement(
    Guid CommandId,
    Guid SourceEntitlementId,
    Guid TargetUnitId,
    Guid? ReplacementLinkId,
    DateOnly ReplacementDate,
    DateTime EventTime,
    string ActorRef,
    string? ReplacementPolicyCode = null,
    DateOnly? ContractDate = null,
    string? Country = null,
    bool ConsumerContract = false,
    string Remedy = "replacement");

public sealed record TransferEntitlementResult(Guid SourceEntitlementId, Guid TargetEntitlementId, Guid RootEntitlementId, string PolicyApplied, DateOnly ComputedEnd, int ChainLength, Guid EventId, bool Replayed);

/// <summary>
/// Sells a separately priced plan for a unit after the sale (P2-T07): an extended term or an add-on in a stacking family,
/// started at the purchase or at the base entitlement's expiry as its policy says, priced and deferred over its term.
/// The source document (the plan's invoice or order) is required: Finance defers revenue against it.
/// </summary>
public sealed record AttachExtendedWarranty(
    Guid CommandId,
    Guid UnitId,
    string CoverageFamily,
    DateOnly ContractDate,
    Guid ObligorEntityId,
    DateTime PurchasedAt,
    decimal Price,
    string Currency,
    DocumentRef SourceDocument,
    DateTime EventTime,
    string ActorRef,
    Guid? PolicyVersionId = null,
    Guid? PayerPartyRef = null,
    string? PayerPartyType = null,
    Guid? OwnerPartyRef = null,
    string? OwnerPartyType = null,
    string? Country = null,
    Guid? SiteLocationId = null,
    string? CustomerPoRef = null);

public sealed record AttachExtendedWarrantyResult(Guid EntitlementId, string PolicyCode, int PolicyVersion, string StartBasis, DateOnly StartDate, DateOnly EndDate, Guid? BaseEntitlementId, IReadOnlyList<EntitlementRevenueSchedulePeriod> RevenueSchedule, Guid EventId, bool Replayed);

/// <summary>
/// Corrects term, end date, coverage or start on one entitlement (P2-T08, ADR-0001 correction table, cases 129 and 140)
/// with a reason code; before and after are kept on EntitlementAmendment and nothing is edited in place. Give either a
/// new end or a new term, not both: a new start or term recomputes the end and keeps any earlier adjustment, a new end
/// sets it outright. A coverage change keeps the service mode; a change of service mode is SupersedeEntitlement.
/// <see cref="CommittedBy"/> is who commits it and the roles they hold, which the approval policy is checked against;
/// the HTTP endpoint always sets it from the signed-in user, whatever the body says. <see cref="EnteredValue"/> is the
/// amendment's value in the tenant's currency, asked for only when the approval tier has an amount bound and the
/// entitlement's policy carries no value of its own (P2-T08b); a policy value always wins over an entered one.
/// </summary>
public sealed record AmendEntitlement(
    Guid CommandId,
    Guid EntitlementId,
    string ReasonCode,
    DateTime EventTime,
    string ActorRef,
    DateOnly? NewStart = null,
    DateOnly? NewEnd = null,
    AmendedTerm? NewTerm = null,
    CoverageTerms? NewCoverage = null,
    DocumentRef? SourceDocument = null,
    string? CustomerNotificationRef = null,
    string? FinanceReviewRef = null,
    string? Note = null,
    AmendmentCommitter? CommittedBy = null,
    decimal? EnteredValue = null);

/// <summary>A new term length; the entitlement keeps its end-of-month rule and whether the term counts its start day.</summary>
public sealed record AmendedTerm(int Value, string Unit);

/// <summary>Who commits an amendment and the roles they hold in the tenant. <see cref="System"/> applies statutory extensions; no signed-in user is ever it.</summary>
public sealed record AmendmentCommitter(string UserRef, IReadOnlyList<string> Roles)
{
    public static readonly AmendmentCommitter System = new("system", []);

    public bool IsSystem => UserRef == System.UserRef && Roles.Count == 0;
}

/// <summary>The amendment as committed; <see cref="Value"/> and <see cref="ValueSource"/> (<c>policy</c> or <c>entered</c>) are null when no value was known or asked for.</summary>
public sealed record AmendEntitlementResult(Guid EntitlementId, DateOnly? StartDate, DateOnly? EndDate, int DaysChanged, bool Reduction, string ApprovalLevel, string ApprovedBy, IReadOnlyList<EntitlementFieldChange> Changes, Guid EventId, bool Replayed, decimal? Value = null, string? ValueSource = null);

/// <summary>
/// Creates the missing base entitlement for each named unit as one atomic command. Validation refuses the whole list by
/// default; <see cref="SkipInvalid"/> explicitly permits valid units to be created while invalid ones are reported.
/// Selection-set expansion supplies <see cref="UnitIds"/> in P2-T11; the per-unit operation is reusable independently.
/// </summary>
public sealed record BackfillEntitlements(
    Guid CommandId,
    IReadOnlyList<Guid> UnitIds,
    string ReasonCode,
    IReadOnlyList<string> EvidenceRefs,
    DocumentRef SourceDocument,
    Guid ObligorEntityId,
    DateTime EventTime,
    string ActorRef,
    AmendmentCommitter? CommittedBy = null,
    bool SkipInvalid = false,
    string? FinanceReviewRef = null,
    string? Country = null,
    Guid? SiteLocationId = null);

public sealed record BackfillEntitlementsResult(IReadOnlyList<BackfillUnitResult> Units, bool Replayed);

public sealed record BackfillUnitResult(Guid UnitId, string Outcome, string? Reason, Guid? EntitlementId, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>
/// The tenant's approval policy for AmendEntitlement (OD-06, Action Item 33b): per reason, whether a source document is
/// required, whether only the system applies it, and tiers by days changed, the first tier whose bound holds deciding.
/// A reduction is raised to <see cref="ReductionRole"/> at least, and needs a customer notification once a certificate
/// was issued. Stored as tenant data; the module's default (the ADR's proposed values) applies until a tenant sets one.
/// </summary>
public sealed record AmendmentApprovalPolicy(IReadOnlyList<AmendmentReasonRule> Reasons, string ReductionRole);

public sealed record AmendmentReasonRule(string ReasonCode, bool SourceDocumentRequired, bool SystemOnly, IReadOnlyList<AmendmentApprovalTier> Tiers);

/// <summary>
/// Up to <see cref="UpToDays"/> days changed (either way; null: any), <see cref="Role"/> commits it, with a finance review
/// reference when <see cref="FinanceReview"/>, or when the amendment's value exceeds <see cref="FinanceReviewAboveAmount"/>
/// (P2-T08b, in the tenant's currency; such a tier cannot be saved or applied until the tenant currency is set up).
/// </summary>
public sealed record AmendmentApprovalTier(int? UpToDays, string Role, bool FinanceReview = false, decimal? FinanceReviewAboveAmount = null);

/// <summary>An entitlement as a reader sees it today: the stored status and the effective one (lazy expiry, never written at read).</summary>
public sealed record EntitlementView(Guid EntitlementId, Guid UnitId, string CoverageFamily, string Type, string StoredStatus, string EffectiveStatus, string? ClosedReason, DateOnly? StartDate, DateOnly? EndDate, int? DaysRemaining, Guid PolicyVersionId, string? StartBasis, string StartTimeZone, Guid? ParentEntitlementId, Guid? RootEntitlementId, Guid? SupersedesEntitlementId);
