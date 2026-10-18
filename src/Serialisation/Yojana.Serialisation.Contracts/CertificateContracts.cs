using System.Text.Json;

namespace Yojana.Serialisation.Contracts;

public static class CertificateFormType
{
    public const string GenericRelease = "generic_release";
    public const string CertificateOfConformity = "coc";
    public const string DeviceHistoryExtract = "dhr_extract";
    public const string Release81303Style = "release_8130_3_style";
    public const string EasaForm1Style = "easa_form_1_style";
    public const string BatteryPassport = "battery_passport";
    public const string WarrantyCertificate = "warranty_certificate";
    public const string Custom = "custom";
    public static readonly IReadOnlyList<string> All = [GenericRelease, CertificateOfConformity, DeviceHistoryExtract,
        Release81303Style, EasaForm1Style, BatteryPassport, WarrantyCertificate, Custom];
}

public static class CertificateStatus
{
    public const string Draft = "draft";
    public const string Issued = "issued";
    public const string Superseded = "superseded";
    public static readonly IReadOnlyList<string> All = [Draft, Issued, Superseded];
}

public static class CertificateSupersedeReason
{
    public const string Repair = "repair";
    public const string Refurbishment = "refurbishment";
    public const string ComponentReplacement = "component_replacement";
    public const string Correction = "correction";
    public const string Reassembly = "reassembly";
    public const string IdentifierChange = "identifier_change";
    public const string EntitlementChange = "entitlement_change";
    public static readonly IReadOnlyList<string> All = [Repair, Refurbishment, ComponentReplacement, Correction, Reassembly, IdentifierChange, EntitlementChange];
}

public static class CertificateSignatureMeaning
{
    public const string Release = "release";
    public const string Review = "review";
    public const string Approval = "approval";
    public const string Authorship = "authorship";
    public static readonly IReadOnlyList<string> All = [Release, Review, Approval, Authorship];
}

public static class CertificateEventType
{
    public const string Issued = "certificate.issued";
    public const string Superseded = "certificate.superseded";
    public const string Withdrawn = "certificate.withdrawn";
    public const string Reprinted = "certificate.reprinted";
}

public static class ReleaseRecordIssuePoint
{
    public const string AtFirstShipment = "at_first_shipment";
    public const string AtBuildRelease = "at_build_release";
    public const string OnRequest = "on_request";
    public static readonly IReadOnlyList<string> All = [AtFirstShipment, AtBuildRelease, OnRequest];
}

public sealed record ReleaseRecordIssuePointOverride(string ItemClass, string IssuePoint);
public sealed record ReleaseRecordIssuePointPolicy(string DefaultIssuePoint,
    IReadOnlyList<ReleaseRecordIssuePointOverride> ItemClassOverrides);
public sealed record ReleaseRecordIssuePointSettingsView(string DefaultIssuePoint,
    IReadOnlyList<ReleaseRecordIssuePointOverride> ItemClassOverrides, string Version);
public sealed record UpdateReleaseRecordIssuePointSettings(string DefaultIssuePoint,
    IReadOnlyList<ReleaseRecordIssuePointOverride> ItemClassOverrides, string ExpectedVersion);

public static class CertificateDatePattern
{
    public const string DayMonthNameYear = "d MMM yyyy";
    public const string DayMonthYear = "dd/MM/yyyy";
    public const string MonthDayYear = "MM/dd/yyyy";
    public const string YearMonthDay = "yyyy-MM-dd";
    public static readonly IReadOnlyList<string> All =
        [DayMonthNameYear, DayMonthYear, MonthDayYear, YearMonthDay];
}

public static class CertificateTimePattern
{
    public const string TwentyFourHour = "HH:mm";
    public const string TwelveHour = "h:mm tt";
    public static readonly IReadOnlyList<string> All = [TwentyFourHour, TwelveHour];
}

public sealed record CertificateDateFormatChoice(string Language, string DatePattern, string TimePattern);
public sealed record CertificateDateFormatPolicy(IReadOnlyList<CertificateDateFormatChoice> Languages);
public sealed record CertificateDateFormatSettingsView(IReadOnlyList<CertificateDateFormatChoice> Languages,
    string Version);
public sealed record UpdateCertificateDateFormatSettings(IReadOnlyList<CertificateDateFormatChoice> Languages,
    string ExpectedVersion);

public static class CertificateTemplateStatus
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Retired = "retired";
    public static readonly IReadOnlyList<string> All = [Draft, Active, Retired];
}

public static class CertificateLayoutKind
{
    public const string Html = "html";
}

public sealed record CertificateTriggerSnapshot(string Schema, Guid EventId, DateTime EffectiveAt,
    string? Reason = null, bool? ConditionReverificationRequired = null, string? Note = null);
public sealed record CertificateProvenanceSnapshot(string Kind, IReadOnlyList<string> Unknowns, IReadOnlyList<string> Evidence);
public sealed record CertificateIssuerSnapshot(string Name, string? RegistrationCode, string? Address = null);
public sealed record CertificateBrandingSnapshot(string Profile, string? DisplayName, string? Mark,
    Guid? LogoAssetId = null, string? LogoSha256 = null, string? LogoMediaType = null);
public sealed record CertificatePresentationSnapshot(string TimeZone, string DatePattern, string TimePattern);
public sealed record CertificateOwnerSnapshot(string Code, string Name, string? Kind, DateTime EffectiveFrom);

public sealed record ReleaseIdentifierSnapshot(
    string Type,
    string Scheme,
    string Value,
    string? Issuer,
    DateTime ValidFrom,
    DateTime? ValidTo,
    string? Replaces,
    string? ReplacedBy);

public sealed record ReleaseModelSnapshot(string Code, string? Name, string? Revision);

public sealed record ReleaseComponentSnapshot(
    string? Slot,
    string Relation,
    string? Serial,
    string? ItemCode,
    string? ItemName,
    string? LotReference,
    decimal? Quantity,
    string? ReleaseStatus,
    string? ReleaseDocument,
    DateTime InstalledAt,
    DateTime? RemovedAt);

public sealed record RequiredSerializedComponentSnapshot(
    string Slot,
    string ItemCode,
    string? ItemName,
    int ExpectedQuantity,
    string Status,
    IReadOnlyList<string> CapturedSerials)
{
    public const string Captured = "CAPTURED";
    public const string Missing = "MISSING";
    public const string UnexpectedItem = "UNEXPECTED_ITEM";
}

public sealed record ReleaseTestSnapshot(string Kind, string Result, JsonElement Measurements, string Equipment, DateTime TestedAt);
public sealed record ReleaseLifeEpisodeSnapshot(string Kind, DateTime StartedAt, DateTime? EndedAt, int? CycleNumber);
public sealed record BatteryPassportSnapshot(
    string? Chemistry,
    string? RatedCapacityKwh,
    string? StateOfHealthPercent,
    string? CarbonFootprintKgCo2e,
    string? ManufacturedAt,
    string? ResponsibleOperator);

/// <summary>
/// The release-record schema deliberately has no bindable party, owner or customer member. The issuer address and the
/// refurbisher are immutable display snapshots, never opaque business-partner references.
/// </summary>
public sealed record ReleaseRecordPayload(
    string SchemaVersion,
    string FormType,
    CertificateTriggerSnapshot Trigger,
    IReadOnlyList<ReleaseIdentifierSnapshot> Identifiers,
    ReleaseModelSnapshot Model,
    string BuildSource,
    DateTime? BuildDate,
    string? Facility,
    IReadOnlyList<string> SourceLots,
    IReadOnlyList<ReleaseComponentSnapshot> AsMaintained,
    IReadOnlyList<RequiredSerializedComponentSnapshot> RequiredSerializedComponents,
    IReadOnlyList<ReleaseTestSnapshot> Tests,
    string Condition,
    int? RefurbishmentCycle,
    string? SanitisationReference,
    string? Firmware,
    string? WorkReference,
    string? Refurbisher,
    ReleaseLifeEpisodeSnapshot? LifeEpisode,
    CertificateIssuerSnapshot Issuer,
    CertificateBrandingSnapshot Branding,
    CertificatePresentationSnapshot Presentation,
    CertificateProvenanceSnapshot Provenance,
    BatteryPassportSnapshot? Battery = null);

public sealed record WarrantyEntitlementSnapshot(
    Guid EntitlementId,
    string CoverageFamily,
    string Type,
    string StartBasis,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string PolicyCode,
    string? PolicyName,
    int PolicyVersion,
    string TermSummary,
    IReadOnlyList<Guid> SupersededLineage);

public sealed record WarrantyCertificatePayload(
    string SchemaVersion,
    CertificateTriggerSnapshot Trigger,
    IReadOnlyList<WarrantyEntitlementSnapshot> Entitlements,
    CertificateOwnerSnapshot Owner,
    CertificateIssuerSnapshot Issuer,
    CertificateBrandingSnapshot Branding,
    CertificatePresentationSnapshot Presentation,
    CertificateProvenanceSnapshot Provenance);

public sealed record CertificateUnitIdentifierFact(Guid IdentifierId, string Type, string Scheme, string Value, Guid? IssuerRef,
    DateTime ValidFrom, DateTime? ValidTo, Guid? SupersedesIdentifierId, Guid? SupersededByIdentifierId);
public sealed record CertificateAssemblyFact(Guid LinkId, string? Slot, string Relation, Guid? ChildUnitId, Guid? ChildItemId,
    Guid? ChildLotId, decimal? Quantity, string? ReleaseStatus, string? ReleaseDocument, DateTime InstalledAt, DateTime? RemovedAt,
    string? ChildSerial = null);
public sealed record CertificateBuildFact(Guid BuildRecordId, int Version, DateTime ReleasedAt, Guid ItemId, Guid? ModelVersionId,
    string? Revision, Guid? FacilityId, string Payload, string PayloadHash, string? ReleaseSignatureRef);
public sealed record CertificateOwnershipFact(Guid EpisodeId, Guid? HolderRef, string? HolderKind, DateTime EffectiveFrom);
public sealed record CertificateUnitFact(Guid UnitId, string LifecycleStatus, string Origin, string HistoryDepth, Guid ItemId,
    Guid? LotId, DateTime CommissionedAt, CertificateBuildFact? Build, CertificateOwnershipFact? Ownership,
    IReadOnlyList<CertificateUnitIdentifierFact> Identifiers, IReadOnlyList<CertificateAssemblyFact> Assembly);

public interface ICertificateUnitReadPort
{
    Task<CertificateUnitFact?> ReadAsync(Guid unitId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> FindUnitIdsBySerialAsync(string serial, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> PrimarySerialsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, Guid>> CurrentItemIdsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);
}

public sealed record CertificateConditionFact(string Grade, IReadOnlyList<ReleaseTestSnapshot> Tests,
    string? SanitisationReference, string? Firmware, BatteryPassportSnapshot? Battery = null);
public interface ICertificateConditionReadPort
{
    Task<CertificateConditionFact> ReadAsync(Guid unitId, CancellationToken cancellationToken);
}

public sealed record CertificateRefurbishmentFact(int? CycleNumber, string? WorkReference, Guid? RefurbisherRef,
    ReleaseLifeEpisodeSnapshot? LifeEpisode);
public interface ICertificateRefurbishmentReadPort
{
    Task<CertificateRefurbishmentFact> ReadAsync(Guid unitId, CancellationToken cancellationToken);
}

public sealed record CertificateReplacementFact(IReadOnlyDictionary<Guid, string> Replaces, IReadOnlyDictionary<Guid, string> ReplacedBy);
public interface ICertificateReplacementReadPort
{
    Task<CertificateReplacementFact> ReadAsync(Guid unitId, CancellationToken cancellationToken);
}

public sealed record CertificateEntitlementFact(Guid EntitlementId, string CoverageFamily, string Type, string Status,
    string? StartBasis, DateOnly? StartDate, DateOnly? EndDate, string PolicyCode, string PolicyName, int PolicyVersion,
    int DurationValue, string DurationUnit, string Coverage, Guid? SupersedesEntitlementId, Guid? RootEntitlementId,
    IReadOnlyList<Guid> SupersededLineage);
public interface ICertificateEntitlementReadPort
{
    Task<IReadOnlyList<CertificateEntitlementFact>> LiveAsync(Guid unitId, CancellationToken cancellationToken);
}

public sealed record CertificateSignatureRow(Guid SignatureId, string PrintedName, string Meaning, DateTime SignedAt,
    string SignedAtDisplay, string HashSigned, string Method);
public sealed record CertificateReprintRow(Guid ReprintId, int Version, string RequestedBy, DateTime At,
    string AtDisplay, string? Recipient);
public sealed record CertificateDetail(Guid CertificateId, Guid UnitId, Guid? ItemId, string PrimarySerial, string DocumentKind,
    string FormType, int Version, string TrackingNumber, Guid TemplateVersionId, Guid? AsBuiltRevisionId,
    JsonElement Payload, string PayloadHash, Guid? SupersedesCertificateId, string? SupersedeReason,
    Guid TriggeringEventId, CertificateIssuerSnapshot Issuer, CertificateOwnerSnapshot? Owner,
    CertificateBrandingSnapshot Branding, string Language, string Provenance, IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Evidence, DateTime IssuedAt, string IssuedAtDisplay, string Status,
    IReadOnlyList<CertificateSignatureRow> Signatures, IReadOnlyList<CertificateReprintRow> Reprints);
public sealed record CertificateListRow(Guid CertificateId, Guid UnitId, Guid? ItemId, string PrimarySerial, string DocumentKind,
    string FormType, int Version, string TrackingNumber, string Status, DateTime IssuedAt, string IssuedAtDisplay,
    string Provenance, string? IssuedBy = null);
public sealed record CertificateVersionChain(IReadOnlyList<CertificateListRow> ReleaseRecords,
    IReadOnlyList<CertificateListRow> WarrantyCertificates);

public sealed record CertificateRenderSignature(string PrintedName, string Meaning, DateTime SignedAt, string Method);
public sealed record CertificateRenderMetadata(string TrackingNumber, int Version, DateTime IssuedAt,
    string? SupersedesTrackingNumber, string? SupersedeReason, string PayloadHash, string VerifyReference);
public sealed record CertificateRenderRequest(string Payload, Guid TemplateVersionId, bool Copy = false,
    CertificateRenderSignature? Signature = null, CertificateRenderMetadata? Metadata = null,
    string? DocumentKind = null);
public sealed record CertificateRenderResult(byte[] Bytes, string MediaType);
public interface ICertificateRenderer
{
    Task<CertificateRenderResult> RenderAsync(CertificateRenderRequest request, CancellationToken cancellationToken);
}

public sealed record IssueCertificateVersion(Guid TriggeringEventId, Guid UnitId, string DocumentKind, string FormType,
    string? SupersedeReason, DateTime EffectiveAt, string ActorRef, Guid IssuerEntityId,
    string Provenance = CertificateProvenance.SystemGenerated, IReadOnlyList<string>? Unknowns = null,
    IReadOnlyList<string>? Evidence = null, string Language = "en", string? TriggerSchema = null);

public sealed record IssueCertificateVersionResult(bool Issued, bool Replayed, Guid? CertificateId, int? Version,
    string? TrackingNumber, string? RefusalRule = null, string? RefusalDetail = null, bool Absorbed = false);

public static class CertificateSelectionOutcome
{
    public const string IssueVersion1 = "issue_version_1";
    public const string ReprintCopy = "reprint_copy";
    public const string NotIssuable = "not_issuable";
}

public sealed record CertificateSelectionPreviewRequest(Guid SelectionId, string DocumentKind, string FormType,
    Guid IssuerEntityId, string Language, bool IncludeBundle = true, int Skip = 0, int Take = 50,
    string? Find = null, string? Outcome = null);

public sealed record CertificateSelectionUnit(Guid UnitId, string? Serial, Guid? ItemId, string? ItemCode,
    string? ItemName, string? Revision, Guid? CertificateId, int? CertificateVersion, string? TrackingNumber,
    string Outcome, string? Reason, string? Detail, string? UnblockingStep);

public sealed record CertificateSelectionKpis(int InSelection, int NewVersion1, int ReprintAsCopy,
    int NotIssuable, int OutputDocuments, bool Bundle);

public sealed record CertificateSelectionPreview(Guid SelectionId, CertificateSelectionKpis Kpis,
    IReadOnlyList<CertificateSelectionUnit> Units, int FilteredTotal, int Skip, int Take);

public sealed record CertificateOutputEntry(Guid UnitId, string Serial, Guid CertificateId, int Version,
    string TrackingNumber, string FileName, string MediaType, bool Copy);

public sealed record CertificateRunResult(Guid CommandId, bool Replayed, CertificateSelectionKpis Kpis,
    IReadOnlyList<CertificateOutputEntry> Documents, IReadOnlyList<CertificateSelectionUnit> Skipped,
    byte[] Archive, string ArchiveMediaType, string ArchiveFileName);

/// <summary>Issues missing version-one certificates and reprints current ones for one stored selection.</summary>
public sealed record IssueCertificate(Guid CommandId, Guid SelectionId, string DocumentKind, string FormType,
    Guid IssuerEntityId, string Language, string ReprintReason, bool IncludeBundle, string ActorRef = "");

/// <summary>Reprints current certificates only; no certificate version is created.</summary>
public sealed record ReprintCertificate(Guid CommandId, Guid SelectionId, string DocumentKind,
    string Reason, bool IncludeBundle, string ActorRef = "");

public sealed record CertificateTriggerContext(Guid UnitId, string Schema, Guid EventId, DateTime EventTime,
    DateTime RecordedTime, long Seq, Guid LegalEntityId, string ActorRef, DocumentRef? Document);

public sealed record CertificateFieldCatalogueEntry(string Path, string Type, bool Repeating, string? Description = null);
public sealed record CertificateTemplateSummary(Guid TemplateId, string Code, string Name, string DocumentKind,
    string FormType, string? AppliesToItemClass, bool BuiltIn, int LatestVersion, string LatestStatus,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, string? ApprovedBy, DateTime? ApprovedAt, int IssuedCount);
public sealed record CertificateTemplateVersionRow(Guid TemplateVersionId, int Version, string Status, string Language,
    string PayloadSchemaVersion, DateTime? EffectiveFrom, DateTime? EffectiveTo, string LayoutKind, string LayoutBody,
    string PageSize, string Orientation, decimal MarginTopMm, decimal MarginRightMm, decimal MarginBottomMm,
    decimal MarginLeftMm, string? HeaderBody, string? FooterBody, Guid? BrandingAssetId,
    IReadOnlyDictionary<string, Guid> BrandingAssets,
    IReadOnlyDictionary<string, string> StaticText, IReadOnlyList<string> Fields, string? ApprovedBy,
    DateTime? ApprovedAt, DateTime CreatedAt, string CreatedBy, DateTime UpdatedAt, string UpdatedBy);
public sealed record CertificateTemplateDetail(Guid TemplateId, string Code, string Name, string DocumentKind,
    string FormType, string? AppliesToItemClass, bool BuiltIn, IReadOnlyList<CertificateTemplateVersionRow> Versions);
public sealed record CreateCertificateTemplateRequest(string Code, string Name, string DocumentKind, string FormType,
    string? AppliesToItemClass, Guid? CopyFromTemplateId = null);
public sealed record SaveCertificateTemplateDraftRequest(Guid? TemplateVersionId, string Language,
    string PayloadSchemaVersion, string LayoutBody, string PageSize, string Orientation, decimal MarginTopMm,
    decimal MarginRightMm, decimal MarginBottomMm, decimal MarginLeftMm, string? HeaderBody, string? FooterBody,
    Guid? BrandingAssetId, IReadOnlyDictionary<string, Guid> BrandingAssets,
    IReadOnlyDictionary<string, string> StaticText, IReadOnlyList<string> Fields);
public sealed record CertificateTemplateValidation(bool Valid, IReadOnlyList<string> UnknownPlaceholders,
    IReadOnlyList<string> PartyFieldViolations, IReadOnlyList<string> DeclaredButUnusedFields,
    IReadOnlyList<string> UnknownHelpers, IReadOnlyList<string> Errors);
public sealed record CertificateTemplatePreviewRequest(Guid SampleUnitId);
public sealed record CertificateTemplatePreview(byte[] Bytes, string MediaType, CertificateTemplateValidation Validation);

public static class TenantAssetKind
{
    public const string Logo = "logo";
}

public sealed record TenantAssetRow(Guid AssetId, string Kind, Guid? LegalEntityId, string MediaType, string Sha256,
    int Width, int Height, int ByteLength, string UploadedBy, DateTime UploadedAt, string? OriginalFileName = null);
public sealed record TenantAssetContent(Guid AssetId, string MediaType, byte[] Bytes, string Sha256, DateTime UploadedAt);
public sealed record UploadTenantLogo(Guid CommandId, Guid? LegalEntityId, byte[] Bytes, string ActorRef = "",
    string? OriginalFileName = null);
public sealed record TenantAssetUploadResult(TenantAssetRow Asset, bool Replayed);
