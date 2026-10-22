using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Data.Entities;

public sealed class KnowledgeEntry : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntryId { get; set; }
    public required string Title { get; set; }
    public required string Problem { get; set; }
    public required string Answer { get; set; }
    public required string Steps { get; set; }
    public required string WrittenAgainstJson { get; set; }
    public required string Status { get; set; }
    public Guid SourceQuestionId { get; set; }
    public int CurrentVersion { get; set; }
    public int? PublishedVersion { get; set; }
    public bool NeedsReview { get; set; }
    public DateTime? NeedsReviewSince { get; set; }
    public Guid AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid LastSavedBy { get; set; }
    public DateTime LastSavedAt { get; set; }
    public Guid? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public Guid? RetiredBy { get; set; }
    public DateTime? RetiredAt { get; set; }
    public string? RetirementNote { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class KnowledgeEntryVersion : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntryId { get; set; }
    public int Version { get; set; }
    public required string Title { get; set; }
    public required string Problem { get; set; }
    public required string Answer { get; set; }
    public required string Steps { get; set; }
    public required string WrittenAgainstJson { get; set; }
    public Guid SavedBy { get; set; }
    public DateTime SavedAt { get; set; }
}

public sealed class KnowledgeEntryTag : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntryId { get; set; }
    public int Version { get; set; }
    public required string Kind { get; set; }
    public required string Value { get; set; }
    public required string Label { get; set; }
}

public sealed class KnowledgeEntryRecordLink : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntryId { get; set; }
    public int Version { get; set; }
    public required string RecordKind { get; set; }
    public Guid RecordId { get; set; }
}

public sealed class KnowledgeEntryReviewRound : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntryId { get; set; }
    public int Round { get; set; }
    public int Version { get; set; }
    public Guid SubmittedBy { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string? Decision { get; set; }
    public Guid? Reviewer { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? Note { get; set; }
}

public sealed class KnowledgeEntryWrittenAgainst : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntryId { get; set; }
    public int Version { get; set; }
    public required string Kind { get; set; }
    public required string Reference { get; set; }
    public required string Label { get; set; }
}

/// <summary>An append-only reason with its later clearance, preserving why a published entry was rechecked.</summary>
public sealed class KnowledgeEntryReviewReason : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReasonId { get; set; }
    public Guid EntryId { get; set; }
    public required string Kind { get; set; }
    public required string Reference { get; set; }
    public required string Label { get; set; }
    public required string Detail { get; set; }
    public Guid TriggerId { get; set; }
    public DateTime FlaggedAt { get; set; }
    public Guid? ClearedBy { get; set; }
    public DateTime? ClearedAt { get; set; }
    public int? ClearedAtVersion { get; set; }
    public string? Clearance { get; set; }
}

/// <summary>Immutable tenant-administrator audit record for a screen contract changing.</summary>
public sealed class KnowledgeScreenChange : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ChangeId { get; set; }
    public required string ScreenReference { get; set; }
    public required string ScreenLabel { get; set; }
    public required string Reason { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

public sealed class TrainingPath : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PathId { get; set; }
    public required string Role { get; set; }
    public required string Introduction { get; set; }
    public int CurrentVersion { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid LastChangedBy { get; set; }
    public DateTime LastChangedAt { get; set; }
}

public sealed class TrainingPathVersion : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PathId { get; set; }
    public int Version { get; set; }
    public required string Role { get; set; }
    public required string Introduction { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

public sealed class TrainingPathEntry : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PathId { get; set; }
    public int Version { get; set; }
    public int Position { get; set; }
    public Guid EntryId { get; set; }
}

public sealed class TrainingProgress : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PathId { get; set; }
    public Guid EntryId { get; set; }
    public Guid UserId { get; set; }
    public int ReadEntryVersion { get; set; }
    public DateTime ReadAt { get; set; }
}

public sealed class TrainingWelcomeDismissal : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PathId { get; set; }
    public Guid UserId { get; set; }
    public DateTime DismissedAt { get; set; }
}
