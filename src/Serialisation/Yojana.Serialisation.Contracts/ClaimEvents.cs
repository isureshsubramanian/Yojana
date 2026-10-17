namespace Yojana.Serialisation.Contracts;

public sealed record PublishedClaimLineAmount(Guid ClaimLineId, decimal RequestedAmount, decimal ApprovedAmount, decimal CoveragePct, string CoverageSource, Guid? EntitlementId);

public sealed record ClaimAdjudicated(
    PublishedEventHeader Header,
    Guid ClaimId,
    Guid? CohortId,
    string CoverageSource,
    IReadOnlyList<PublishedClaimLineAmount> ApprovedAmountsByLine,
    Guid ServiceEntityId,
    Guid ObligorEntityId) : IPublishedUnitEvent
{
    public const string Schema = "Claim.Adjudicated";
    public const string Version = "1.0";
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

