using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Privacy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>A tenant's customer e-mail template (P9-T20): a name and a kind; the wording lives in its versions.</summary>
public sealed class MailTemplate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>The built-in layout or template it was copied from, as a name shown on the list.</summary>
    public string? StartedFrom { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

/// <summary>
/// One version of a template. Drafts are edited in place (with <see cref="RowVersion"/>); a published or retired version
/// is never changed again except to retire it, and every sent message names the version it was made from.
/// </summary>
public sealed class MailTemplateVersion : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid VersionId { get; set; }
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public required string Status { get; set; }
    /// <summary>The <c>MailTemplateContent</c> as JSON: languages, blocks, attachment and note choices.</summary>
    public required string Content { get; set; }
    public bool SubmittedForReview { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>The template a kind of message uses by default in one language.</summary>
public sealed class MailTemplateDefault : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Kind { get; set; }
    public required string Language { get; set; }
    public Guid TemplateId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>
/// One customer e-mail as it was sent: the rendered copy, the template version, the record it was about and its delivery
/// state. The id is the platform mail id, so <c>MailDeliveryRecorded</c> updates the row it belongs to. The copy is kept
/// here, not in the platform delivery log, because the log deliberately holds no bodies (account mail carries single-use
/// links) while a customer message must be shown exactly as sent.
/// </summary>
[PersonalData]
public sealed class CustomerMailMessage : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>Messages sent together from one compose (one per To recipient) share a batch.</summary>
    public Guid BatchId { get; set; }
    /// <summary>The compose command, so a repeated request does not send twice.</summary>
    public Guid CommandId { get; set; }
    public required string Kind { get; set; }
    public Guid TemplateId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public int TemplateVersion { get; set; }
    public required string TemplateName { get; set; }
    public required string Language { get; set; }
    public Guid? UnitId { get; set; }
    public required string AboutLabel { get; set; }
    public Guid? CustomerPartyRef { get; set; }
    public string? CustomerName { get; set; }
    public required string ToAddress { get; set; }
    public string? ToName { get; set; }
    /// <summary>JSON array of {address, name}.</summary>
    public string Cc { get; set; } = "[]";
    public required string Subject { get; set; }
    public required string HtmlBody { get; set; }
    public required string TextBody { get; set; }
    public string? AttachmentName { get; set; }
    public bool HasNote { get; set; }
    /// <summary>The user who sent it; null when the product sent it on its own.</summary>
    public string? SentBy { get; set; }
    public bool CopyToSelf { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? Transport { get; set; }
}
