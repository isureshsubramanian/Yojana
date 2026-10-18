using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class CertificateInstance : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CertificateId { get; set; }
    public Guid UnitId { get; set; }
    public required string DocumentKind { get; set; }
    public required string FormType { get; set; }
    public int Version { get; set; }
    public required string TrackingNumber { get; set; }
    public Guid TemplateVersionId { get; set; }
    public Guid? AsBuiltRevisionId { get; set; }
    public required string Payload { get; set; }
    public required string PayloadHash { get; set; }
    public required string FactsHash { get; set; }
    public Guid? SupersedesCertificateId { get; set; }
    public string? SupersedeReason { get; set; }
    public Guid TriggeringEventId { get; set; }
    public Guid IssuerEntityId { get; set; }
    public required string IssuerSnapshot { get; set; }
    public string? OwnerSnapshot { get; set; }
    public required string BrandingSnapshot { get; set; }
    public required string Language { get; set; }
    public required string Provenance { get; set; }
    public string Unknowns { get; set; } = "[]";
    public string Evidence { get; set; } = "[]";
    public DateTime IssuedAt { get; set; }
    public required string Status { get; set; }
}

public sealed class CertificateTrigger : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UnitId { get; set; }
    public required string DocumentKind { get; set; }
    public Guid TriggeringEventId { get; set; }
    public required string Outcome { get; set; }
    public Guid? CertificateId { get; set; }
    public string? RefusalRule { get; set; }
    public string? RefusalDetail { get; set; }
    public DateTime RecordedAt { get; set; }
}

public sealed class CertificateSignature : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid SignatureId { get; set; }
    public Guid CertificateId { get; set; }
    public required string SignerIdentity { get; set; }
    public required string PrintedName { get; set; }
    public required string Meaning { get; set; }
    public DateTime SignedAt { get; set; }
    public required string HashSigned { get; set; }
    public required string SignatureMethod { get; set; }
}

public sealed class ReprintLog : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReprintId { get; set; }
    public Guid CertificateId { get; set; }
    public int Version { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime At { get; set; }
    public Guid? RecipientPartyRef { get; set; }
}

public sealed class CertificateTemplate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string DocumentKind { get; set; }
    public required string FormType { get; set; }
    public string? AppliesToItemClass { get; set; }
    public bool BuiltIn { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

public sealed class CertificateTemplateVersion : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public required string Language { get; set; }
    public required string PayloadSchemaVersion { get; set; }
    public required string Status { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public required string LayoutKind { get; set; }
    public required string LayoutBody { get; set; }
    public required string PageSize { get; set; }
    public required string Orientation { get; set; }
    public decimal MarginTopMm { get; set; }
    public decimal MarginRightMm { get; set; }
    public decimal MarginBottomMm { get; set; }
    public decimal MarginLeftMm { get; set; }
    public string? HeaderBody { get; set; }
    public string? FooterBody { get; set; }
    public string? BrandingAssetRef { get; set; }
    public Guid? BrandingAssetId { get; set; }
    public string StaticText { get; set; } = "{}";
    public string Fields { get; set; } = "[]";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

public sealed class TenantAsset : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid AssetId { get; set; }
    public required string Kind { get; set; }
    public Guid? LegalEntityId { get; set; }
    public required string MediaType { get; set; }
    public required byte[] Bytes { get; set; }
    public required string Sha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}
