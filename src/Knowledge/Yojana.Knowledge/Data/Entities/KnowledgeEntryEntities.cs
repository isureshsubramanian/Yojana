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
