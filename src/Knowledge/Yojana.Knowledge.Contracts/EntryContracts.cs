using Yojana.Platform.Contracts.Records;

namespace Yojana.Knowledge.Contracts;

public static class KnowledgeEntryStatus
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Published = "published";
    public const string Retired = "retired";

    public static readonly IReadOnlyList<string> All = [Draft, InReview, Published, Retired];
}

public static class KnowledgeTagKind
{
    public const string Role = "role";
    public const string Task = "task";
    public const string ItemFamily = "item_family";
    public const string ClaimCause = "claim_cause";

    public static readonly IReadOnlyList<string> All = [Role, Task, ItemFamily, ClaimCause];
}

public static class WrittenAgainstKind
{
    public const string PolicyVersion = "policy_version";
    public const string Screen = "screen";

    public static readonly IReadOnlyList<string> All = [PolicyVersion, Screen];
}

public sealed record EntryTagInput(string Kind, string Value);

public sealed record EntryRecordLinkInput(string RecordKind, Guid RecordId);

public sealed record WrittenAgainstInput(string Kind, string Reference, string Label);

public sealed record EntryDraftInput(string Title, string Problem, string Answer, string Steps,
    IReadOnlyList<EntryTagInput> Tags, IReadOnlyList<EntryRecordLinkInput> RecordLinks,
    IReadOnlyList<WrittenAgainstInput> WrittenAgainst);

public sealed record CreateEntryFromAnswer(Guid QuestionId, QuestionActor Actor);

public sealed record EditDraft(Guid EntryId, EntryDraftInput Draft, QuestionActor Actor);

public sealed record SubmitEntryForReview(Guid EntryId, QuestionActor Actor);

public sealed record ApproveEntry(Guid EntryId, string? Note, QuestionActor Actor);

public sealed record SendBackEntry(Guid EntryId, string Note, QuestionActor Actor);

public sealed record RetireEntry(Guid EntryId, string Note, QuestionActor Actor);

public sealed record EntryChangeResult(Guid EntryId, string Status, int CurrentVersion);

public sealed record KnowledgeEntryCreated(Guid EntryId, Guid SourceQuestionId, Guid AuthorId, DateTime CreatedAt);

public sealed record KnowledgeEntryVersionSaved(Guid EntryId, int Version, Guid SavedBy, DateTime SavedAt);

public sealed record KnowledgeEntrySubmitted(Guid EntryId, int Version, int Round, Guid SubmittedBy, DateTime SubmittedAt);

public sealed record KnowledgeEntryApproved(Guid EntryId, int Version, int Round, Guid Reviewer, DateTime ReviewedAt);

public sealed record KnowledgeEntrySentBack(Guid EntryId, int Version, int Round, Guid Reviewer, DateTime ReviewedAt,
    string Note);

public sealed record KnowledgeEntryRetired(Guid EntryId, Guid RetiredBy, DateTime RetiredAt, string Note);
