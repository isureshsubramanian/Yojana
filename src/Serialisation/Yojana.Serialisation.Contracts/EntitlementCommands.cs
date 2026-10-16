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

/// <summary>An entitlement as a reader sees it today: the stored status and the effective one (lazy expiry, never written at read).</summary>
public sealed record EntitlementView(Guid EntitlementId, Guid UnitId, string CoverageFamily, string Type, string StoredStatus, string EffectiveStatus, string? ClosedReason, DateOnly? StartDate, DateOnly? EndDate, int? DaysRemaining, Guid PolicyVersionId, string? StartBasis, string StartTimeZone, Guid? ParentEntitlementId, Guid? RootEntitlementId, Guid? SupersedesEntitlementId);
