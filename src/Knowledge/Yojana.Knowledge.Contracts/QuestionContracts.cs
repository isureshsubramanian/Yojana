namespace Yojana.Knowledge.Contracts;

public static class QuestionStatus
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string ClosedWithoutAnswer = "closed_without_answer";

    public static readonly IReadOnlyList<string> All = [Open, Answered, ClosedWithoutAnswer];
}

/// <summary>Trusted command identity. HTTP endpoints always replace it with the current claims.</summary>
public sealed record QuestionActor(Guid UserId, IReadOnlyList<string> Roles);

public sealed record AskQuestion(Guid RecordId, string RecordKind, string Title, string Body,
    Guid? DirectedToUser, string? DirectedToRole, QuestionActor Actor);

public sealed record AskQuestionResult(Guid QuestionId, string Status);

public sealed record ReplyToQuestion(Guid QuestionId, string Body, QuestionActor Actor);

public sealed record ReplyToQuestionResult(Guid QuestionId, Guid ReplyId, string Status);

public sealed record EditQuestion(Guid QuestionId, string Body, QuestionActor Actor);

public sealed record EditReply(Guid QuestionId, Guid ReplyId, string Body, QuestionActor Actor);

public sealed record AcceptQuestionAnswer(Guid QuestionId, Guid ReplyId, QuestionActor Actor);

public sealed record CloseQuestionWithoutAnswer(Guid QuestionId, string Reason, QuestionActor Actor);

public sealed record ReopenQuestion(Guid QuestionId, QuestionActor Actor);

public sealed record QuestionChangeResult(Guid QuestionId, string Status);

public sealed record QuestionAsked(Guid QuestionId, string RecordKind, Guid RecordId, Guid AskedBy,
    Guid? DirectedToUser, string? DirectedToRole, DateTime AskedAt);

public sealed record QuestionReplied(Guid QuestionId, Guid ReplyId, Guid RepliedBy, DateTime RepliedAt);

public sealed record QuestionAnswered(Guid QuestionId, Guid ReplyId, Guid AnsweredBy, DateTime AnsweredAt);

public sealed record QuestionClosed(Guid QuestionId, Guid ClosedBy, DateTime ClosedAt, string Reason);

public sealed record QuestionReopened(Guid QuestionId, Guid ReopenedBy, DateTime ReopenedAt);
