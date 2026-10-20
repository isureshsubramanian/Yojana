namespace Yojana.Serialisation.Contracts;

public static class ClaimStatus
{
    public const string Submitted = "submitted";
    public const string Adjudicating = "adjudicating";
    public const string Approved = "approved";
    public const string PartiallyApproved = "partially_approved";
    public const string Denied = "denied";
    public const string Settled = "settled";
    public const string Closed = "closed";
}

public static class ClaimEventType
{
    public const string Submitted = "claim.submitted";
    public const string Adjudicating = "claim.adjudicating";
    public const string Adjudicated = "claim.adjudicated";
    public const string Settled = "claim.settled";
    public const string Closed = "claim.closed";
    public const string RecoveryRaised = "recovery_claim.raised";
    public const string RecoveryApproved = "recovery_claim.approved";
    public const string ChargeableQuoteRequested = "chargeable_quote.requested";
    public const string EvidenceRequested = "claim.evidence_requested";
}

public static class ClaimCoverageSource
{
    public const string Warranty = "warranty";
    public const string Recall = "recall";
    public const string Goodwill = "goodwill";
    public const string Contract = "contract";
    public const string Chargeable = "chargeable";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Warranty, Recall, Goodwill, Contract, Chargeable], StringComparer.Ordinal);
}

public static class ClaimLineKind
{
    public const string Parts = "parts";
    public const string Labour = "labour";
    public const string Travel = "travel";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Parts, Labour, Travel], StringComparer.Ordinal);
}

public static class ClaimRemedy
{
    public const string Repair = "repair";
    public const string Replacement = "replacement";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Repair, Replacement], StringComparer.Ordinal);
}

public static class ClaimDenialReason
{
    public const string ItemServiceBlocked = "item_service_blocked";
    public const string AfterLastServiceDate = "after_last_service_date";
    public const string ClaimWaitingPeriod = "claim_waiting_period";
}

public static class ClaimWaitingPeriodNotAppliedReason
{
    public const string PurchaseDateUnavailable = "purchase_date_unavailable";
}

public static class ClaimCampaignCoverReason
{
    public const string Ended = "campaign_ended";
    public const string Closed = "campaign_closed";
}

/// <summary>Findings an adjudicator may deny a whole claim on; the policy stack's own reasons are never chosen by hand.</summary>
public static class ClaimManualDenialReason
{
    public const string UnauthorisedPartsDamage = "unauthorised_parts_damage";
    public const string MisuseOrAccident = "misuse_or_accident";
    public const string NoFaultFound = "no_fault_found";
    public const string EvidenceInsufficient = "evidence_insufficient";
    public const string TamperedOrMissingLabel = "tampered_or_missing_label";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([UnauthorisedPartsDamage, MisuseOrAccident, NoFaultFound, EvidenceInsufficient, TamperedOrMissingLabel], StringComparer.Ordinal);
}

public static class ClaimEvidenceKind
{
    public const string Attachment = "attachment";
    public const string ProofOfPurchase = "proof_of_purchase";
    public const string ConsumerRemedyChoice = "consumer_remedy_choice";
    public const string AdjudicationNote = "adjudication_note";
    public const string EvidenceRequested = "evidence_requested";
}

public static class RecoveryClaimStatus
{
    public const string Raised = "raised";
    public const string Approved = "approved";
}

