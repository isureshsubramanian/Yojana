using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>The stable identity and applicability rule shared by all versions of one business label.</summary>
public sealed class LabelTemplate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public Guid CreateCommandId { get; set; }
    public required string Name { get; set; }
    public required string AppliesTo { get; set; }
    public required string AppliesToValue { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

/// <summary>
/// Business-effective and SQL system-versioned label definition. Draft content may be saved; active content is immutable
/// and a later change is another row with a higher <see cref="Version"/>.
/// </summary>
public sealed class LabelTemplateVersion : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public required string LayoutKind { get; set; }
    public required string Layout { get; set; }
    public string StaticText { get; set; } = "[]";
    public string Bindings { get; set; } = "[]";
    public required string Symbology { get; set; }
    public Guid? PreviewSampleUnitId { get; set; }
    public DateTime? PreviewedAt { get; set; }
    public string? PreviewedBy { get; set; }
    public string? PreviewedContentHash { get; set; }
    public bool LegalReview { get; set; }
    public DateTime? LegalReviewRequestedAt { get; set; }
    public string? LegalReviewRequestedBy { get; set; }
    public DateTime? LegalReviewedAt { get; set; }
    public string? LegalReviewedBy { get; set; }
    public string? LegalReviewContentHash { get; set; }
    public bool Regulated { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ApprovalContentHash { get; set; }
    public required string Status { get; set; }
    public required string ContentHash { get; set; }
    /// <summary>JSON array of command ids: makes every draft/lifecycle command replay-safe without another table.</summary>
    public string CommandIds { get; set; } = "[]";
    public DateTime RecordedAt { get; set; }
    public required string ActorRef { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? RetiredAt { get; set; }
    public string? RetiredBy { get; set; }
}

/// <summary>One attempted printer delivery, including rejected attempts and immutable-payload reprints.</summary>
public sealed class PrintJob : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid JobId { get; set; }
    public Guid CommandId { get; set; }
    public Guid? BatchCommandId { get; set; }
    public int? BatchSequence { get; set; }
    public string? RequestedSerial { get; set; }
    public Guid? SourceEventId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? IdentifierId { get; set; }
    public Guid? OriginalJobId { get; set; }
    public string? RenderedPayload { get; set; }
    public string? PayloadHash { get; set; }
    public string? PrinterRef { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public required string Status { get; set; }
    public string? RejectionRule { get; set; }
    public string? RejectionReason { get; set; }
    public string? EmptyRequiredField { get; set; }
    public DateTime? RenderedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public Guid? LedgerEventId { get; set; }
}
