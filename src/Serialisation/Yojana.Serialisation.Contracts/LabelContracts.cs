namespace Yojana.Serialisation.Contracts;

public static class LabelTemplateAppliesTo
{
    public const string ItemFamily = "item_family";
    public const string IdentifierType = "identifier_type";
    public static readonly IReadOnlyList<string> All = [ItemFamily, IdentifierType];
}

public static class LabelLayoutKind
{
    public const string Zpl = "zpl";
    public const string Html = "html";
    public static readonly IReadOnlyList<string> All = [Zpl, Html];
}

public static class LabelSymbology
{
    public const string Code128 = "code128";
    public const string DataMatrix = "datamatrix";
    public const string Qr = "qr";
    public const string Code39 = "code39";
    public static readonly IReadOnlyList<string> All = [Code128, DataMatrix, Qr, Code39];
}

public static class LabelTemplateStatus
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Retired = "retired";
    public static readonly IReadOnlyList<string> All = [Draft, Active, Retired];
}

public static class LabelPrintStatus
{
    public const string Rendered = "rendered";
    public const string Sent = "sent";
    public const string Confirmed = "confirmed";
    public const string Downloaded = "downloaded";
    public const string Rejected = "rejected";
    public static readonly IReadOnlyList<string> All = [Rendered, Sent, Confirmed, Downloaded, Rejected];
}

public static class LabelOutputFormat
{
    public const string Sheet = "sheet";
    public const string OnePerPage = "one_per_page";
    public static readonly IReadOnlyList<string> All = [Sheet, OnePerPage];
}

public static class LabelPrinterAdapter
{
    public const string PdfDownload = "pdf_download";
    public const string Unavailable = "unavailable";
    public static readonly IReadOnlyList<string> All = [PdfDownload, Unavailable];
}

public static class LabelPageSize
{
    public const string A4 = "A4";
    public const string Letter = "Letter";
    public static readonly IReadOnlyList<string> All = [A4, Letter];
}

public static class LabelPrintJobKind
{
    public const string Manual = "manual";
    public const string Reprint = "reprint";
    public const string Event = "event";
    public static readonly IReadOnlyList<string> All = [Manual, Reprint, Event];
}

public static class LabelEventType
{
    public const string Printed = "label.printed";
    public const string Reprinted = "label.reprinted";
}

/// <summary>The fixed field catalogue is enforced by the label service; clients never submit executable expressions.</summary>
public static class LabelBindableField
{
    public const string UnitLifecycleStatus = "unit.lifecycle_status";
    public const string UnitExpiryDate = "unit.expiry_date";
    public const string ItemCode = "item.code";
    public const string ItemName = "item.name";
    public const string ItemRevision = "item.revision";
    public const string IdentifierValue = "identifier.value";
    public const string ManufacturerSerial = "identifier.manufacturer_serial";
    public const string InternalSerial = "identifier.internal_serial";
    public const string VendorSerial = "identifier.vendor_serial";
    public const string CustomerAssetTag = "identifier.customer_asset_tag";
    public const string AsBuiltRevision = "as_built.revision";
    public const string AsBuiltBuildDate = "as_built.build_date";
    public const string BaseEntitlementEndDate = "entitlement.base.end_date";
    public const string PrintedAt = "label.printed_at";
    public const string LookupUrl = "lookup.url";

    public static readonly IReadOnlyList<string> All =
    [
        UnitLifecycleStatus, UnitExpiryDate, ItemCode, ItemName, ItemRevision, IdentifierValue,
        ManufacturerSerial, InternalSerial, VendorSerial, CustomerAssetTag, AsBuiltRevision,
        AsBuiltBuildDate, BaseEntitlementEndDate, PrintedAt, LookupUrl,
    ];
}

public sealed record LabelBinding(string Placeholder, string FieldPath, bool Required);

public sealed record LabelResolvedBinding(string Placeholder, string FieldPath, bool Required, string? Value);

public sealed record CreateLabelTemplate(
    Guid CommandId,
    string Name,
    string AppliesTo,
    string AppliesToValue,
    string ActorRef);

public sealed record LabelTemplateCreated(Guid TemplateId, bool Replayed);

public sealed record SaveLabelTemplateDraft(
    Guid CommandId,
    Guid TemplateId,
    Guid? TemplateVersionId,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string LayoutKind,
    string Layout,
    IReadOnlyList<string> StaticText,
    IReadOnlyList<LabelBinding> Bindings,
    string Symbology,
    Guid? PreviewSampleUnitId,
    bool Regulated,
    string ActorRef,
    decimal? WidthMm = null,
    decimal? HeightMm = null,
    int? DpiHint = null);

public sealed record LabelTemplateVersionSaved(Guid TemplateId, Guid TemplateVersionId, int Version, bool LegalReview, bool Replayed);

public sealed record PreviewLabelTemplate(Guid TemplateVersionId, Guid? SampleUnitId = null, string ActorRef = "");

public sealed record LabelTemplatePreview(
    Guid TemplateVersionId,
    Guid UnitId,
    string LayoutKind,
    string? RenderedPayload,
    IReadOnlyList<LabelResolvedBinding> Bindings,
    string? EmptyRequiredField,
    string? RejectionReason,
    BarcodeEncoding? Barcode,
    decimal? WidthMm = null,
    decimal? HeightMm = null);

public sealed record RequestLabelLegalReview(Guid CommandId, Guid TemplateVersionId, string ActorRef);

public sealed record SignLabelLegalReview(Guid CommandId, Guid TemplateVersionId, string ActorRef);

public sealed record ApproveRegulatedLabel(Guid CommandId, Guid TemplateVersionId, string ActorRef);

public sealed record ActivateLabelTemplateVersion(Guid CommandId, Guid TemplateVersionId, DateTime ActivatedAt, string ActorRef);

public sealed record RetireLabelTemplateVersion(Guid CommandId, Guid TemplateVersionId, DateTime RetiredAt, string ActorRef);

public sealed record LabelTemplateVersionChanged(Guid TemplateVersionId, string Status, bool LegalReview, bool Replayed);

public sealed record PrintLabel(
    Guid CommandId,
    Guid UnitId,
    Guid? TemplateId,
    Guid? IdentifierId,
    string PrinterRef,
    DateTime PrintAt,
    Guid LegalEntityId,
    string ActorRef,
    Guid? SourceEventId = null,
    Guid? TemplateVersionId = null,
    Guid? BatchCommandId = null,
    int? BatchSequence = null,
    string? RequestedSerial = null,
    LabelPrintLayout? PrintLayout = null);

public sealed record ReprintLabel(
    Guid CommandId,
    Guid JobId,
    string? PrinterRef,
    DateTime ReprintedAt,
    Guid LegalEntityId,
    string ActorRef,
    string Reason = "",
    LabelPrintLayout? PrintLayout = null);

public sealed record LabelPrintResult(
    Guid JobId,
    string JobNumber,
    string Status,
    string? RejectionReason,
    string? PayloadHash,
    bool Replayed,
    string? RejectionRule = null,
    string? EmptyRequiredField = null,
    LabelDownload? Download = null);

public sealed record BatchPrintLabels(
    Guid CommandId,
    Guid? TemplateVersionId,
    string PrinterRef,
    IReadOnlyList<string> Serials,
    DateTime PrintAt,
    string ActorRef,
    LabelPrintLayout? PrintLayout = null);

public sealed record LabelBatchPrintOutcome(
    string Serial,
    Guid? UnitId,
    Guid? JobId,
    string Status,
    string? RejectionRule,
    string? RejectionReason,
    string? EmptyRequiredField);

public sealed record LabelBatchPrintResult(
    Guid CommandId,
    IReadOnlyList<LabelBatchPrintOutcome> Outcomes,
    bool Replayed,
    LabelDownload? Download = null);

public sealed record LabelPrintLayout(
    string Format,
    string? SheetProfileId = null,
    int StartRow = 1,
    int StartColumn = 1,
    int Copies = 1);

public sealed record LabelDownload(byte[] Bytes, string MediaType, string FileName, bool HtmlFallback = false);

public sealed record LabelSheetProfile(
    string ProfileId,
    string Name,
    string PageSize,
    int Columns,
    int Rows,
    decimal LabelWidthMm,
    decimal LabelHeightMm,
    decimal MarginTopMm,
    decimal MarginRightMm,
    decimal MarginBottomMm,
    decimal MarginLeftMm,
    decimal HorizontalGapMm,
    decimal VerticalGapMm,
    bool BuiltIn = false);

public sealed record LabelTemplateCatalogueRow(
    Guid TemplateId,
    string Name,
    string AppliesTo,
    string AppliesToValue,
    int VersionCount,
    int? ActiveVersion,
    Guid? ActiveTemplateVersionId,
    DateTime CreatedAt,
    string CreatedBy,
    int? LatestVersion,
    string? LatestStatus,
    DateTime? EffectiveFrom,
    DateTime? EffectiveTo,
    bool LegalReview,
    string? LegalReviewedBy,
    DateTime? ApprovedAt,
    int PrintedCount,
    decimal? ActiveWidthMm = null,
    decimal? ActiveHeightMm = null);

public sealed record LabelTemplateVersionRow(
    Guid TemplateVersionId,
    Guid TemplateId,
    int Version,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string LayoutKind,
    string Layout,
    IReadOnlyList<string> StaticText,
    IReadOnlyList<LabelBinding> Bindings,
    string Symbology,
    Guid? PreviewSampleUnitId,
    bool LegalReview,
    string? LegalReviewedBy,
    DateTime? LegalReviewedAt,
    bool Regulated,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    bool PreviewCurrent,
    bool LegalReviewCurrent,
    bool ApprovalCurrent,
    string Status,
    DateTime RecordedAt,
    string ActorRef,
    decimal? WidthMm = null,
    decimal? HeightMm = null,
    int? DpiHint = null);

public sealed record LabelTemplateDetail(
    Guid TemplateId,
    string Name,
    string AppliesTo,
    string AppliesToValue,
    DateTime CreatedAt,
    string CreatedBy,
    IReadOnlyList<LabelTemplateVersionRow> Versions);

public sealed record LabelPrintJobRow(
    Guid JobId,
    string JobNumber,
    Guid? TemplateVersionId,
    Guid? UnitId,
    Guid? IdentifierId,
    Guid? OriginalJobId,
    string? TemplateName,
    int? TemplateVersion,
    string? Serial,
    string? RenderedPayload,
    string? PayloadHash,
    string? PrinterRef,
    string RequestedBy,
    DateTime RequestedAt,
    string Status,
    string? RejectionRule,
    string? RejectionReason,
    string? EmptyRequiredField,
    DateTime? SentAt,
    DateTime? ConfirmedAt,
    Guid? SourceEventId,
    DateTime? DownloadedAt = null,
    string? OutputFormat = null,
    string? SheetProfileId = null,
    int? StartRow = null,
    int? StartColumn = null,
    int Copies = 1,
    string? ReprintReason = null);

public sealed record LabelPrintJobPage(IReadOnlyList<LabelPrintJobRow> Rows, int Total, int Skip, int Take);

public sealed record LabelPrintJobSummary(
    int Printed,
    int Confirmed,
    int Downloaded,
    int Waiting,
    int Rejected,
    int Reprints,
    int FromEvents,
    DateTime From,
    DateTime To);

public sealed record LabelPolicy(
    IReadOnlyList<string> LegalReviewPhrases,
    bool PrintExpiryDates = false,
    string DefaultPrinterRef = "pdf_download",
    string PrinterAdapter = LabelPrinterAdapter.PdfDownload,
    string DefaultFormat = LabelOutputFormat.Sheet,
    LabelSheetProfile? DefaultSheet = null,
    IReadOnlyList<LabelSheetProfile>? ExtraSheetProfiles = null);

public sealed record LabelPrintOptions(
    string DefaultFormat,
    IReadOnlyList<LabelSheetProfile> SheetProfiles,
    string DefaultSheetProfileId);

public sealed record LabelBindableFieldRow(string FieldPath, bool ExpiryControlled = false);

public sealed record LabelPrinted(
    PublishedEventHeader Header,
    Guid JobId,
    Guid TemplateVersionId,
    string LayoutKind,
    string Symbology,
    string PayloadHash,
    string PrinterRef) : IPublishedUnitEvent
{
    public const string Schema = "Label.Printed";
    public const string Version = "1.0";
}

public sealed record LabelReprinted(
    PublishedEventHeader Header,
    Guid JobId,
    Guid OriginalJobId,
    Guid TemplateVersionId,
    string LayoutKind,
    string Symbology,
    string PayloadHash,
    string PrinterRef) : IPublishedUnitEvent
{
    public const string Schema = "Label.Reprinted";
    public const string Version = "1.0";
}
