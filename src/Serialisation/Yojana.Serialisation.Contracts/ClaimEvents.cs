namespace Yojana.Serialisation.Contracts;

public sealed record PublishedClaimLineAmount(Guid ClaimLineId, decimal RequestedAmount, decimal ApprovedAmount, decimal CoveragePct, string CoverageSource, Guid? EntitlementId);

public sealed record ClaimAdjudicated(
    PublishedEventHeader Header,
    Guid ClaimId,
    Guid? CohortId,
    string CoverageSource,
    IReadOnlyList<PublishedClaimLineAmount> ApprovedAmountsByLine,
    Guid ServiceEntityId,
    Guid ObligorEntityId,
    Guid ClaimantPartyRef,
    Guid SettlementPartyRef,
    Guid? SuccessorItemId = null,
    Guid? CampaignId = null,
    string? CampaignCode = null,
    string? ReserveTag = null,
    string? CampaignCoverReason = null,
    string? Currency = null) : IPublishedUnitEvent
{
    public const string Schema = "Claim.Adjudicated";
    public const string Version = "1.3";
}

public sealed record RecoveryClaimRaised(
    PublishedEventHeader Header,
    Guid RecoveryClaimId,
    Guid CustomerClaimId,
    Guid ClaimLineId,
    Guid InboundEntitlementId,
    Guid ChildUnitId,
    Guid VendorPartyRef,
    decimal RequestedAmount,
    string Currency) : IPublishedUnitEvent
{
    public const string Schema = "RecoveryClaim.Raised";
    public const string Version = "1.0";
}

public sealed record RecoveryClaimApproved(
    PublishedEventHeader Header,
    Guid RecoveryClaimId,
    Guid VendorPartyRef,
    decimal Amount,
    string Currency) : IPublishedUnitEvent
{
    public const string Schema = "RecoveryClaim.Approved";
    public const string Version = "1.0";
}

public sealed record ChargeableQuoteLine(Guid ClaimLineId, string Kind, decimal UncoveredAmount, string? Reason);

public sealed record ChargeableQuoteRequested(
    PublishedEventHeader Header,
    Guid ClaimId,
    Guid ClaimantPartyRef,
    Guid SettlementPartyRef,
    string Currency,
    IReadOnlyList<ChargeableQuoteLine> Lines) : IPublishedUnitEvent
{
    public const string Schema = "ChargeableQuote.Requested";
    public const string Version = "1.0";
}

/// <summary>
/// Transactional-outbox request for P6's certificate service. P4 records the truthful reconstruction provenance and
/// evidence only; it does not create a certificate instance, payload, signature or fictional build history.
/// </summary>
public sealed record CertificateIssueRequested(
    Guid RequestId,
    Guid UnitId,
    string DocumentKind,
    string Provenance,
    Guid TriggeringClaimId,
    IReadOnlyList<string> EvidenceRefs,
    DateTime RequestedAt,
    Guid LegalEntityId,
    string ActorRef);

public static class CertificateDocumentKind
{
    public const string ReleaseRecord = "release_record";
    public const string WarrantyCertificate = "warranty_certificate";
    public static readonly IReadOnlyList<string> All = [ReleaseRecord, WarrantyCertificate];
}

public static class CertificateProvenance
{
    public const string SystemGenerated = "system_generated";
    public const string Migrated = "migrated";
    public const string Reconstructed = "reconstructed";
    public static readonly IReadOnlyList<string> All = [SystemGenerated, Migrated, Reconstructed];
}

