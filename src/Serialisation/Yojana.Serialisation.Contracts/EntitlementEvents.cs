namespace Yojana.Serialisation.Contracts;

// Accrual cohorts identify one assurance-warranty accrual by obligor, item, sale period and tag: creation assigns the
// cohort and Finance accrues it once. Inherited entitlements created by transfer or replacement carry no cohort and set
// AccrualSuppressed so Finance never posts a second accrual. A separately priced extended warranty is deferred revenue
// over the schedule published by ExtendedSold, not an accrual. Finance's ledgers remain the system of record for amounts;
// this module retains only posting references received in its reply event. Consumers are idempotent on Header.EventId.

public sealed record EntitlementCreated(PublishedEventHeader Header, Guid EntitlementId, Guid ItemId, string CoverageFamily, string EntitlementType, Guid ObligorEntityId, Guid? OwnerPartyRef, Guid PolicyVersionId, string PolicyCode, int PolicyVersion, DateOnly? StartDate, DateOnly? EndDate, string Status, Guid? AccrualCohortId, string AssuranceOrService, bool AccrualSuppressed, string Provenance, DocumentRef? SourceDocument) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Created";
    public const string Version = "1.0";
}

public sealed record EntitlementExtendedSold(PublishedEventHeader Header, Guid EntitlementId, Guid ItemId, string CoverageFamily, Guid ObligorEntityId, Guid? PayerPartyRef, decimal Price, string Currency, int TermMonths, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<EntitlementRevenueSchedulePeriod> RevenueSchedule, DocumentRef SourceDocument) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.ExtendedSold";
    public const string Version = "1.0";
}

public sealed record EntitlementRevenueSchedulePeriod(DateOnly PeriodStart, DateOnly PeriodEnd, decimal Amount);

public sealed record EntitlementTransferred(PublishedEventHeader Header, Guid SourceEntitlementId, Guid TargetEntitlementId, Guid SourceUnitId, Guid TargetUnitId, Guid? ReplacementLinkId, string PolicyApplied, DateOnly ComputedEnd, Guid RootEntitlementId, int ChainLength, bool AccrualSuppressed) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Transferred";
    public const string Version = "1.0";
}

public sealed record EntitlementRebased(PublishedEventHeader Header, Guid EntitlementId, DateOnly? OldStart, DateOnly NewStart, DateOnly? OldEnd, DateOnly NewEnd, string Basis, string? EvidenceRef, bool PolicyCapApplied) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Rebased";
    public const string Version = "1.0";
}

public sealed record EntitlementAmended(PublishedEventHeader Header, Guid EntitlementId, string ReasonCode, int? DaysChanged, string? ApprovedBy, string? CustomerNotificationRef, IReadOnlyList<EntitlementFieldChange> Changes, DocumentRef? SourceDocument) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Amended";
    public const string Version = "1.0";
}

public sealed record EntitlementFieldChange(string Field, string? Before, string? After);

public sealed record EntitlementSuperseded(PublishedEventHeader Header, Guid SupersededEntitlementId, Guid NewEntitlementId, string CoverageFamily, DateOnly EffectiveDate, Guid OldPolicyVersionId, Guid NewPolicyVersionId, DocumentRef? SourceDocument) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Superseded";
    public const string Version = "1.0";
}

public sealed record EntitlementBackfilled(PublishedEventHeader Header, Guid EntitlementId, string StartBasis, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<string> EvidenceRefs, Guid? AccrualCohortId, bool FinanceReviewRequired, DocumentRef? SourceDocument) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Backfilled";
    public const string Version = "1.0";
}

public sealed record EntitlementClosed(PublishedEventHeader Header, Guid EntitlementId, string CoverageFamily, string ClosedReason, DateOnly ClosedEffective, Guid? SuccessorEntitlementId, Guid? SuccessorUnitId) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.Closed";
    public const string Version = "1.0";
}

public sealed record EntitlementCoverageChanged(PublishedEventHeader Header, IReadOnlyList<Guid> LiveEntitlementIdsBefore, IReadOnlyList<Guid> LiveEntitlementIdsAfter, DateOnly? WarrantyEndMaxBefore, DateOnly? WarrantyEndMaxAfter, string Cause) : IPublishedUnitEvent
{
    public const string Schema = "Entitlement.CoverageChanged";
    public const string Version = "1.0";
}

public sealed record FinanceEntitlementPostingRecorded(Guid MessageId, Guid EntitlementId, Guid SourceEventId, string SourceSchema, string PostingReference, DateTime PostedAt, Guid LedgerEntityId, decimal? Amount, string? Currency)
{
    public const string Schema = "Finance.EntitlementPostingRecorded";
    public const string Version = "1.0";
}
