namespace Yojana.Serialisation.Contracts;

/// <summary>The stable scope vocabulary of the version-one customer application API.</summary>
/// <remarks>COUPLING (scope vocabulary): Yojana.Developer.Contracts.PublicApiScopes (the API client and token side)
/// holds the same names; change both together. PublicApiScopeVocabularyTests in the Developer test project fails when
/// the two lists differ.</remarks>
public static class PublicApiScopes
{
    public const string UnitsRead = "units.read";
    public const string CoverRead = "cover.read";
    public const string ClaimsRead = "claims.read";
    public const string ClaimsWrite = "claims.write";
    public const string RmaRead = "rma.read";
    public const string RmaWrite = "rma.write";
    public const string CertificatesRead = "certificates.read";
    public const string EventsRead = "events.read";
    public const string UnitsWriteInstallation = "units.write-installation";
    public const string UnitsWriteAssetTag = "units.write-asset-tag";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UnitsRead, CoverRead, ClaimsRead, ClaimsWrite, RmaRead, RmaWrite, CertificatesRead, EventsRead,
        UnitsWriteInstallation, UnitsWriteAssetTag,
    };
}

public static class PublicApiDisclosureTier
{
    public const string Customer = "customer";
    public const string BackOffice = "back_office";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Customer, BackOffice], StringComparer.Ordinal);
}

public sealed record PublicApiPage<T>(IReadOnlyList<T> Items, string? Next);
public sealed record PublicApiIdentifier(Guid Id, string Type, string Value, bool Current, Guid? ReplacedById);
public sealed record PublicApiAsBuiltPart(Guid? UnitId, Guid? ItemId, string? ItemCode, string? ItemName,
    decimal? Quantity, string Relation, string? Slot);
public sealed record PublicApiAsBuilt(int Version, DateTime ReleasedAt, IReadOnlyList<PublicApiAsBuiltPart> Parts);
public sealed record PublicApiUnit(Guid Id, string LifecycleStatus, Guid ItemId, string ItemCode, string? ItemName,
    string? Condition, DateOnly? ManufactureMonth, DateTime? DeliveryDate,
    IReadOnlyList<PublicApiIdentifier> Identifiers, PublicApiAsBuilt? AsBuilt);

public sealed record PublicApiCoveredUnit(Guid UnitId, string Scope, Guid? ItemId, string? ItemCode, string? ItemName);
public sealed record PublicApiCover(Guid Id, Guid UnitId, string CoverageFamily, string Type, string Status,
    DateOnly? StartDate, DateOnly? EndDate, string? StartBasis, Guid? OwnerPartyId, Guid ObligorEntityId,
    IReadOnlyList<PublicApiCoveredUnit> Covers);
public sealed record PublicApiUnitCover(Guid UnitId, IReadOnlyList<PublicApiCover> Cover);

public sealed record PublicApiClaimLine(Guid Id, Guid UnitId, string Kind, string CoverageFamily,
    string? Description, decimal RequestedAmount, decimal ApprovedAmount, string CoverageSource,
    string? DenialReason);
public sealed record PublicApiClaimEvidence(Guid Id, string Kind, string Reference, string? Description,
    DateTime RecordedAt, string? FileName, string? ContentType);
public sealed record PublicApiClaim(Guid Id, string Number, Guid UnitId, DateOnly ClaimDate, string Status,
    string Remedy, string CoverageSource, string ServiceCountry, string Currency, Guid ClaimantPartyId,
    Guid SettlementPartyId, Guid ServiceEntityId, Guid ObligorEntityId, string? ReportedCauseCode,
    string? ReportedDescription, IReadOnlyList<PublicApiClaimLine> Lines,
    IReadOnlyList<PublicApiClaimEvidence> Evidence);

public sealed record PublicApiRmaLine(Guid Id, Guid UnitId, string CoverageFamily, string Kind,
    string? Description, decimal RequestedAmount, decimal? ApprovedAmount, string? CoverageSource,
    string? DenialReason, string? Disposition);
public sealed record PublicApiRma(Guid Id, string? Number, string Stage, DateOnly RequestDate,
    Guid ReturningPartyId, Guid SettlementPartyId, Guid ServiceEntityId, Guid ObligorEntityId,
    string ServiceCountry, string Currency, IReadOnlyList<PublicApiRmaLine> Lines);
public sealed record PublicApiReplacement(Guid Id, Guid OriginalUnitId, Guid ReplacementUnitId,
    string State, string Reason, DateTime CreatedAt, DateTime? ShippedAt, DateOnly? ReturnDue,
    Guid? CounterpartyPartyId);

public sealed record PublicApiCertificate(Guid Id, Guid UnitId, string Kind, string FormType, int Version,
    string TrackingNumber, string Status, DateTime IssuedAt, Guid? SupersedesId);
public sealed record PublicApiDocument(byte[] Bytes, string ContentType, string FileName);

public sealed record PublicApiDisclosurePolicy(IReadOnlySet<string> VisibleFields);

/// <summary>
/// Read-only Serialisation boundary for the public API. Developer receives API-shaped records and never a
/// Serialisation entity or queryable; customerPartyId is claim-derived and is checked on every resource read.
/// </summary>
public interface IPublicApiReadPort
{
    Task<PublicApiUnit?> UnitAsync(Guid unitId, Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiPage<PublicApiUnit>> UnitsByIdentifierAsync(string identifier, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiUnitCover?> CoverAsync(Guid unitId, Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiClaim?> ClaimAsync(Guid claimId, Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiPage<PublicApiClaim>> ClaimsAsync(Guid unitId, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiRma?> RmaAsync(Guid rmaId, Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiPage<PublicApiRma>> RmasAsync(Guid unitId, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiReplacement?> ReplacementAsync(Guid replacementId, Guid? customerPartyId,
        CancellationToken cancellationToken);
    Task<PublicApiPage<PublicApiReplacement>> ReplacementsAsync(Guid unitId, string? after, int limit,
        Guid? customerPartyId, CancellationToken cancellationToken);
    Task<PublicApiCertificate?> CertificateAsync(Guid certificateId, Guid? customerPartyId,
        CancellationToken cancellationToken);
    Task<PublicApiDocument?> CertificatePdfAsync(Guid certificateId, Guid? customerPartyId,
        CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> VisibleUnitIdsAsync(IReadOnlyList<Guid> unitIds, Guid? customerPartyId,
        CancellationToken cancellationToken);
    Task<PublicApiDisclosurePolicy> DisclosureAsync(string tier, CancellationToken cancellationToken);
}

public sealed record PublicRmaRequest(Guid UnitId, Guid? CustomerPartyId, DateOnly RequestDate,
    string ServiceCountry, string CoverageFamily, string ClaimLineKind, string Description);
public sealed record PublicRmaResult(Guid Id, string Number, string Stage);

public sealed record PublicClaimLineRequest(string Kind, string CoverageFamily, decimal RequestedAmount,
    Guid? UnitId = null, string? Description = null,
    IReadOnlyDictionary<string, decimal>? Measurements = null);
public sealed record PublicEvidenceAttachment(string FileName, string ContentType, byte[] Content);
public sealed record PublicClaimEvidenceRequest(string Kind, string Reference, string? Description = null,
    PublicEvidenceAttachment? Attachment = null);
public sealed record PublicClaimRequest(Guid UnitId, Guid? CustomerPartyId, DateOnly ClaimDate,
    string ServiceCountry, IReadOnlyList<PublicClaimLineRequest> Lines,
    IReadOnlyList<PublicClaimEvidenceRequest> Evidence, string Remedy, string ConsumerChoiceEvidenceRef,
    string? ReportedCauseCode = null, string? ReportedDescription = null, DateOnly? OccurredDate = null);
public sealed record PublicClaimResult(Guid Id, Guid UnitId, string Status, IReadOnlyList<Guid> LineIds);

public sealed record PublicInstallationDateRequest(Guid? CustomerPartyId, DateTime EvidenceAt,
    string? EvidenceReference = null, PublicEvidenceAttachment? Attachment = null);
public sealed record PublicInstallationDateResult(Guid RegistrationId, Guid UnitId, string Outcome,
    string Status, DateOnly? StartDate);
public sealed record PublicAssetTagRequest(Guid? CustomerPartyId, string AssetTag);
public sealed record PublicAssetTagResult(Guid UnitId, Guid IdentifierId, string AssetTag,
    string LifecycleStatus);

/// <summary>
/// Mutation boundary for Developer. Implementations must invoke the established Serialisation commands; the public
/// API supplies neither actor nor business command id, which are stamped by the trusted adapter.
/// </summary>
public interface IPublicApiWritePort
{
    Task<PublicRmaResult?> RequestRmaAsync(Guid commandId, PublicRmaRequest request, Guid? boundCustomerPartyId,
        string actorRef, DateTime eventTime, CancellationToken cancellationToken);
    Task<PublicClaimResult?> SubmitClaimAsync(Guid commandId, PublicClaimRequest request,
        Guid? boundCustomerPartyId, string actorRef, DateTime eventTime, CancellationToken cancellationToken);
    Task<PublicInstallationDateResult?> SetInstallationDateAsync(Guid commandId, Guid unitId,
        PublicInstallationDateRequest request, Guid? boundCustomerPartyId, string actorRef,
        CancellationToken cancellationToken);
    Task<PublicAssetTagResult?> AddAssetTagAsync(Guid commandId, Guid unitId, PublicAssetTagRequest request,
        Guid? boundCustomerPartyId, string actorRef, DateTime eventTime, CancellationToken cancellationToken);
}
