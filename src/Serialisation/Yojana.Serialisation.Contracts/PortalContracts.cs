using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Contracts;

public static class DisclosureTier
{
    public const string Public = "public";
    public const string Owner = "owner";
    public const string Internal = "internal";
    public static readonly IReadOnlyList<string> All = [Public, Owner, Internal];
}

/// <summary>The fixed field catalogue tenants classify; public assignments are restricted to <see cref="PublicSafe"/>.</summary>
public static class PortalDisclosureField
{
    public const string IdentifierValue = "identifier.value";
    public const string IdentifierType = "identifier.type";
    public const string ItemCode = "item.code";
    public const string ItemName = "item.name";
    public const string Condition = "unit.condition";
    public const string ManufactureMonth = "unit.manufacture_month";
    public const string WarrantyStatus = "entitlement.status";
    public const string IdentifierLineage = "identifier.lineage";
    public const string DeliveryDate = "unit.delivery_date";
    public const string CoverageExpiry = "entitlement.expiry";
    public const string ServiceHistory = "service.history";
    public const string Certificates = "certificate.viewer_copy";
    public const string PartyData = "party.data";
    public const string DocumentReferences = "document.references";
    public const string ServiceInternalNotes = "service.internal_notes";
    public const string ServiceCost = "service.cost";

    public static readonly IReadOnlySet<string> PublicSafe = new HashSet<string>(StringComparer.Ordinal)
    {
        IdentifierValue, IdentifierType, ItemCode, ItemName, Condition, ManufactureMonth, WarrantyStatus,
    };

    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [IdentifierValue] = DisclosureTier.Public,
        [IdentifierType] = DisclosureTier.Public,
        [ItemCode] = DisclosureTier.Public,
        [ItemName] = DisclosureTier.Public,
        [Condition] = DisclosureTier.Public,
        [ManufactureMonth] = DisclosureTier.Public,
        [WarrantyStatus] = DisclosureTier.Public,
        [IdentifierLineage] = DisclosureTier.Owner,
        [DeliveryDate] = DisclosureTier.Owner,
        [CoverageExpiry] = DisclosureTier.Owner,
        [ServiceHistory] = DisclosureTier.Owner,
        [Certificates] = DisclosureTier.Owner,
        [PartyData] = DisclosureTier.Internal,
        [DocumentReferences] = DisclosureTier.Internal,
        [ServiceInternalNotes] = DisclosureTier.Internal,
        [ServiceCost] = DisclosureTier.Internal,
    };
}

public sealed record DisclosurePolicyRow(string Field, string Tier, bool Enabled);
public sealed record DisclosurePolicyView(bool PublicTierEnabled, IReadOnlyList<DisclosurePolicyRow> Fields,
    string SettingsVersion);

/// <summary>Read-only disclosure policy boundary for modules that render references to serialisation cases.</summary>
public interface IDisclosurePolicyReadPort
{
    Task<DisclosurePolicyView> ReadAsync(CancellationToken cancellationToken);
}
public sealed record UpdateDisclosureFields(IReadOnlyList<DisclosurePolicyRow> Fields);
public sealed record UpdatePublicPortalSetting(bool Enabled, string ExpectedVersion);

/// <summary>
/// Closed anonymous contract: manufacture month is the sole date-like value; it intentionally has no party,
/// day-level date, document, service, cost or internal-note member.
/// A public policy may only suppress members of this contract; it cannot expand the contract.
/// </summary>
public sealed record PublicPortalResolution(
    string Outcome,
    string? Identifier,
    string? IdentifierType,
    string? ItemCode,
    string? ItemName,
    string? Condition,
    string? ManufactureMonth,
    string? WarrantyStatus,
    string AuthenticatedEntryPoint);

public static class PublicPortalOutcome
{
    public const string Found = "found";
    public const string NotFound = "not_found";
    public const string AuthenticationRequired = "authentication_required";
    public const string Ambiguous = "ambiguous";
}

/// <summary>The external identity boundary. No endpoint accepts either value from the request body.</summary>
public sealed record PortalPrincipal(TenantId TenantId, Guid PartyRef);
public sealed record PortalContext(string OrganisationName);

public sealed record PortalIdentifierLineage(string Value, string Type, bool Current, string? ReplacedBy);
public sealed record PortalCoverageExpiry(string CoverageFamily, DateOnly? Start, DateOnly? Expiry, string Status);
public sealed record PortalUnitSummary(Guid UnitId, string Serial, string LifecycleStatus, string ItemCode,
    string? ItemName, string? Condition, IReadOnlyList<PortalCoverageExpiry> Coverage,
    IReadOnlyList<PortalIdentifierLineage> Identifiers);
public sealed record PortalLookupResponse(string Outcome, IReadOnlyList<PortalUnitSummary> Units);

public sealed record PortalInstalledBaseRow(Guid UnitId, string Serial, string ItemCode, string? ItemName,
    DateTime DeliveryDate, string? CustomerPoRef, string? CustomerItemRef, bool IsComponent,
    IReadOnlyList<PortalCoverageExpiry> Coverage);
public sealed record PortalInstalledBasePage(IReadOnlyList<PortalInstalledBaseRow> Rows, bool HasMore,
    DateTime? NextDeliveryDate, Guid? NextUnitId);

public sealed record PortalServiceHistoryRow(Guid EventId, string EventType, DateTime At, string? Status);
public sealed record PortalServiceHistoryPage(IReadOnlyList<PortalServiceHistoryRow> Rows, bool HasMore,
    DateTime? NextAt, long? NextSequence);

public sealed record PortalSerialInput(string Value, string? IdentifierType = null);
public sealed record PortalSerialListRequest(IReadOnlyList<PortalSerialInput> Serials);
public sealed record PortalSerialMatch(string Input, PortalUnitSummary Unit);
public sealed record PortalInvalidSerial(string Input, string? IdentifierType, IReadOnlyList<string> Reasons);
public sealed record PortalSerialListResult(IReadOnlyList<PortalSerialMatch> Matched,
    IReadOnlyList<string> Unmatched, IReadOnlyList<PortalInvalidSerial> Invalid);

public sealed record PortalWhereUsedParent(Guid UnitId, string Serial, bool Current, DateTime InstalledAt,
    DateTime? RemovedAt, IReadOnlyList<PortalCoverageExpiry> LiveEntitlements);
public sealed record PortalWhereUsedResult(string Outcome, IReadOnlyList<string> ValidationReasons,
    Guid? ComponentUnitId, IReadOnlyList<PortalWhereUsedParent> Parents,
    IReadOnlyList<PortalCoverageExpiry> ComponentEntitlements);

public sealed record PortalCertificateRow(Guid CertificateId, string Kind, string FormType, int Version,
    string TrackingNumber, string Status, DateTime IssuedAt, CertificateOwnerSnapshot? Owner);
public sealed record PortalCertificatePage(IReadOnlyList<PortalCertificateRow> Rows, bool HasMore,
    DateTime? NextIssuedAt, Guid? NextCertificateId);

public static class PortalRegistrationEvidenceKind
{
    public const string Delivery = "delivery";
    public const string Installation = "installation";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Delivery, Installation], StringComparer.Ordinal);
}

public static class PortalRegistrationStatus
{
    public const string Recorded = "recorded";
    public const string PendingReview = "pending_review";
    public const string Approved = "approved";
}

public static class PortalRegistrationOutcome
{
    public const string RegistrationRecorded = "registration_recorded";
    public const string ReviewPending = "review_pending";
    public const string EntitlementRebased = "entitlement_rebased";
}

public sealed record PortalEvidenceAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Portal request shape. The unit and actor come from the route and authenticated claims.</summary>
public sealed record PortalRegistrationRequest(
    Guid CommandId,
    string EvidenceKind,
    DateTime EvidenceAt,
    string? EvidenceReference = null,
    PortalEvidenceAttachment? Attachment = null);

public sealed record PortalRegistrationResult(
    Guid RegistrationId,
    Guid UnitId,
    Guid EntitlementId,
    string Outcome,
    string Status,
    DateTime DeliveryAt,
    DateTime EvidenceAt,
    int GraceDays,
    DateOnly? StartDate,
    bool Replayed = false);

public sealed record PortalRegistrationReviewRow(
    Guid RegistrationId,
    Guid UnitId,
    Guid EntitlementId,
    string EvidenceKind,
    DateTime DeliveryAt,
    DateTime EvidenceAt,
    string? EvidenceReference,
    string? AttachmentFileName,
    string Status,
    DateTime SubmittedAt,
    string SubmittedBy);

public sealed record PortalRegistrationReviewPage(
    IReadOnlyList<PortalRegistrationReviewRow> Items,
    string? NextCursor);

public sealed record ApprovePortalRegistrationRequest(Guid CommandId, DateTime EventTime);

public sealed record PortalRegistrationSettingsView(int GraceDays, string Version);
public sealed record UpdatePortalRegistrationSettings(int GraceDays, string ExpectedVersion);

/// <summary>Stage-one portal intake. Ownership parties, entity, currency and actor are resolved server-side.</summary>
public sealed record PortalRmaRequest(
    Guid CommandId,
    DateOnly RequestDate,
    string ServiceCountry,
    string CoverageFamily,
    string ClaimLineKind,
    string Description);

public sealed record PortalRmaResult(
    Guid RmaId,
    string RmaNumber,
    string Stage,
    bool Replayed);

public static class PortalQueryOutcome
{
    public const string Found = "found";
    public const string NotFound = "not_found";
    public const string Ambiguous = "ambiguous";
    public const string Invalid = "invalid";
}

public static class PortalExportFormat
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";
    public const string SpreadsheetUnavailableReason = "spreadsheet_library_not_approved";
}

public static class CustomerListSourceKind
{
    public const string PastedList = "pasted_list";
    public const string ImportJob = "import_job";
    public const string SelectionSet = "selection_set";
}

public static class CustomerListMatchOutcome
{
    public const string Owned = "owned";
    public const string OwnedByAnotherParty = "owned_by_another_party";
    public const string NotFound = "not_found";
    public const string Invalid = "invalid";
}

public static class CustomerAssertionStatus
{
    public const string PendingVerification = "pending_verification";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
    public const string PendingVerificationLabel = "PENDING VERIFICATION";
}

public sealed record CustomerListInput(
    string Value,
    string? IdentifierType = null,
    string? CustomerPoRef = null,
    string? CustomerItemRef = null);

/// <summary>Exactly one source is supplied. Import and selection references are read through their existing boundaries.</summary>
public sealed record CustomerListSource(
    IReadOnlyList<CustomerListInput>? Pasted = null,
    Guid? ImportJobId = null,
    Guid? SelectionId = null);

/// <summary>
/// Starts a durable internal reconciliation. ActorRef is a trusted boundary field and is always replaced from claims.
/// PartyRef is the customer whose supplied list is being reconciled, not the signed-in actor.
/// </summary>
public sealed record ReconcileCustomerList(
    Guid CommandId,
    Guid PartyRef,
    CustomerListSource Source,
    DateTime AssertedAt,
    string ActorRef,
    int Take = 50,
    string? After = null);

/// <summary>HTTP request shape; the actor is deliberately absent and is stamped from the authenticated principal.</summary>
public sealed record ReconcileCustomerListRequest(
    Guid CommandId,
    Guid PartyRef,
    CustomerListSource Source,
    DateTime AssertedAt,
    int Take = 50,
    string? After = null);

public sealed record CustomerListReconciliationRow(
    int Ordinal,
    string Input,
    string? IdentifierType,
    string? NormalisedIdentifier,
    string Outcome,
    IReadOnlyList<string> ValidationReasons,
    string? Unit,
    string? Item,
    string? OtherOwner,
    Guid? AssertionId,
    string? AssertionStatus,
    string? CustomerPoRef,
    string? CustomerItemRef,
    bool? UnitAtCustomerSiteDefault = null);

public sealed record CustomerListReconciliationPage(
    Guid ReconciliationId,
    string SourceKind,
    string Customer,
    int Total,
    int Owned,
    int OwnedByAnotherParty,
    int NotFound,
    int Invalid,
    IReadOnlyList<CustomerListReconciliationRow> Items,
    string? NextCursor,
    bool Replayed = false);

/// <summary>Verification evidence opens authoritative ownership and may move custody from EvidenceDate.</summary>
public sealed record VerifyCustomerAssertion(
    Guid CommandId,
    Guid AssertionId,
    string EvidenceRef,
    DateTime EvidenceDate,
    Guid LegalEntityId,
    string ActorRef,
    IReadOnlyList<string> ActorRoles,
    bool? UnitAtCustomerSite = null);

/// <summary>HTTP request shape; actor and roles never travel from the browser.</summary>
public sealed record VerifyCustomerAssertionRequest(
    Guid CommandId,
    string EvidenceRef,
    DateTime EvidenceDate,
    Guid LegalEntityId,
    bool? UnitAtCustomerSite = null);

public sealed record RejectCustomerAssertion(
    Guid CommandId,
    Guid AssertionId,
    string Reason,
    string ActorRef,
    IReadOnlyList<string> ActorRoles);

/// <summary>HTTP request shape; actor and roles never travel from the browser.</summary>
public sealed record RejectCustomerAssertionRequest(Guid CommandId, string Reason);

public sealed record CustomerAssertionDecision(
    Guid AssertionId,
    string Status,
    string? EvidenceRef,
    string? RejectionReason,
    DateTime DecidedAt,
    string DecidedBy,
    Guid? OwnershipEpisodeId,
    bool Replayed,
    Guid? CustodyEpisodeId = null,
    bool? UnitAtCustomerSite = null);
