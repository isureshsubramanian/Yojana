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
    public string? ClosedReason { get; set; }
    public Guid? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int ReopenedCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
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
