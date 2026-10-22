using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Data.Entities;

public sealed class Question : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid QuestionId { get; set; }
    public required string RecordKind { get; set; }
    public Guid RecordId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required string Status { get; set; }
    public Guid? DirectedToUser { get; set; }
    public string? DirectedToRole { get; set; }
    public Guid AskedBy { get; set; }
    public DateTime AskedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? AcceptedReplyId { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? ClosedReason { get; set; }
    public Guid? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int ReopenedCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Snapshot of a published entry offered by the existing similar-question search when the question was asked.
/// Keeping the rank makes the later report reproducible even when the entry or full-text catalogue changes.
/// </summary>
public sealed class QuestionSimilarEntry : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid QuestionId { get; set; }
    public Guid EntryId { get; set; }
    public int Rank { get; set; }
    public DateTime MatchedAt { get; set; }
}

public sealed class QuestionReply : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReplyId { get; set; }
    public Guid QuestionId { get; set; }
    public required string Body { get; set; }
    public Guid Author { get; set; }
    public DateTime At { get; set; }
    public DateTime? EditedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Earlier question or reply text. Exactly one of QuestionBody or ReplyId identifies what was edited.</summary>
public sealed class QuestionReplyRevision : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid QuestionId { get; set; }
    public Guid? ReplyId { get; set; }
    public bool QuestionBody { get; set; }
    public required string Body { get; set; }
    public Guid RevisedBy { get; set; }
    public DateTime RevisedAt { get; set; }
}

public sealed class QuestionParticipant : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid QuestionId { get; set; }
    public Guid UserId { get; set; }
    public bool Asked { get; set; }
    public bool Directed { get; set; }
    public bool Replied { get; set; }
    public DateTime? LastReadAt { get; set; }
}

public static class QuestionNotificationMode
{
    public const string Immediately = "immediately";
    public const string DailyDigest = "daily_digest";
    public const string Off = "off";
    public static readonly IReadOnlyList<string> All = [Immediately, DailyDigest, Off];
}

public sealed class QuestionNotificationPreference : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string Mode { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One durable delivery decision per question event and recipient.</summary>
public sealed class QuestionNotification : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid? QuestionId { get; set; }
    public Guid? EntryId { get; set; }
    public Guid RecipientUserId { get; set; }
    public required string EventKind { get; set; }
    public Guid EventId { get; set; }
    public required string DeliveryMode { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid? MailId { get; set; }
    public DateOnly? DigestDate { get; set; }
}

public sealed class QuestionDigest : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly DigestDate { get; set; }
    public Guid MailId { get; set; }
    public DateTime QueuedAt { get; set; }
}

/// <summary>Immutable link to bytes owned by Platform. Exactly one of question-body or reply is the attachment target.</summary>
public sealed class QuestionAttachment : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid FileId { get; set; }
    public Guid QuestionId { get; set; }
    public Guid? ReplyId { get; set; }
    public bool QuestionBody { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long ContentLength { get; set; }
    public required string Sha256 { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}

/// <summary>One immutable audit row per completed scheduled retention batch; purged question ids are not retained.</summary>
public sealed class QuestionPurgeRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RunId { get; set; }
    public required string TriggeredByKind { get; set; }
    public Guid? TriggeredByUserId { get; set; }
    public required string TriggeredByLabel { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public DateTime Cutoff { get; set; }
    public int RetentionYears { get; set; }
    public int QuestionsPurged { get; set; }
    public int RepliesPurged { get; set; }
    public int RevisionsPurged { get; set; }
    public int AttachmentsPurged { get; set; }
}

/// <summary>Audit of a personal-data redaction. It records scope and identities, never the removed text.</summary>
public sealed class QuestionPersonalDataRedaction : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RedactionId { get; set; }
    public Guid PersonId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid PerformedByUserId { get; set; }
    public DateTime PerformedAt { get; set; }
    public required string ScopeJson { get; set; }
    public required string Reason { get; set; }
    public int QuestionsRedacted { get; set; }
    public int RepliesRedacted { get; set; }
    public int RevisionsRedacted { get; set; }
    public int AttachmentsPurged { get; set; }
}

/// <summary>Short-lived cross-database hand-off; removed immediately after Platform confirms byte deletion.</summary>
public sealed class QuestionFilePurgePending : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid FileId { get; set; }
    public DateTime QueuedAt { get; set; }
    public required string Reason { get; set; }
}
