using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Knowledge.Questions;

public static class QuestionCommandHandler
{
    public static async Task<AskQuestionResult> Handle(AskQuestion command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantStaffDirectory staff, ITenantContext tenant, IClock clock,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        Required(command.Title, 240, "question_title_required", "question_title_too_long", "Question title");
        Required(command.Body, 8000, "question_body_required", "question_body_too_long", "Question body");
        if ((command.DirectedToUser is null) == (command.DirectedToRole is null))
            throw new QuestionRuleException("question_addressee_exactly_one", "Direct a question to either one person or one role.");

        if (command.DirectedToUser is { } user && !await staff.ActiveStaffMemberExistsAsync(user, cancellationToken))
            throw new QuestionRuleException("directed_user_not_found", "The selected person is not active staff in this tenant.");
        if (command.DirectedToRole is { } role)
        {
            if (!YojanaRole.IsKnown(role))
                throw new QuestionRuleException("directed_role_unknown", $"'{role}' is not a tenant role.");
            if (!await staff.ActiveStaffRoleExistsAsync(role, cancellationToken))
                throw new QuestionRuleException("directed_role_not_present", "No active staff member holds the selected role in this tenant.");
        }

        var viewer = new RecordReferenceViewer(command.Actor.UserId, command.Actor.Roles);
        var record = await records.OneAsync(command.RecordKind, command.RecordId, viewer, cancellationToken);
        if (!record.Exists) throw new QuestionRuleException("record_not_found", "The record does not exist.");
        if (!record.MayView) throw new QuestionRuleException("record_not_visible", "The current user may not view this record.");

        var now = clock.UtcNow;
        var questionId = TimeOrderedId.New(now);
        db.Questions.Add(new Question
        {
            TenantId = tenant.TenantId, QuestionId = questionId, RecordKind = command.RecordKind,
            RecordId = command.RecordId, Title = command.Title.Trim(), Body = command.Body.Trim(),
            Status = QuestionStatus.Open, DirectedToUser = command.DirectedToUser,
            DirectedToRole = command.DirectedToRole, AskedBy = command.Actor.UserId, AskedAt = now,
        });
        db.Participants.Add(new QuestionParticipant
        {
            TenantId = tenant.TenantId, QuestionId = questionId, UserId = command.Actor.UserId, Asked = true,
        });
        if (command.DirectedToUser is { } directed && directed != command.Actor.UserId)
            db.Participants.Add(new QuestionParticipant
            {
                TenantId = tenant.TenantId, QuestionId = questionId, UserId = directed, Directed = true,
            });
        else if (command.DirectedToUser == command.Actor.UserId)
            db.Participants.Local.Single(x => x.QuestionId == questionId && x.UserId == command.Actor.UserId).Directed = true;

        await PublishAsync(bus, tenant, new QuestionAsked(questionId, command.RecordKind, command.RecordId,
            command.Actor.UserId, command.DirectedToUser, command.DirectedToRole, now), cancellationToken);
        return new AskQuestionResult(questionId, QuestionStatus.Open);
    }

    public static async Task<ReplyToQuestionResult> Handle(ReplyToQuestion command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantContext tenant, IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        Required(command.Body, 8000, "reply_body_required", "reply_body_too_long", "Reply body");
        var question = await FindAsync(db, tenant, command.QuestionId, cancellationToken);
        await EnsureVisibleAsync(question, command.Actor, records, cancellationToken);
        if (question.Status != QuestionStatus.Open)
            throw new QuestionRuleException("question_not_open", "Replies can be added only while the question is open.");
        var now = clock.UtcNow;
        var replyId = TimeOrderedId.New(now);
        db.Replies.Add(new QuestionReply
        {
            TenantId = tenant.TenantId, ReplyId = replyId, QuestionId = question.QuestionId,
            Body = command.Body.Trim(), Author = command.Actor.UserId, At = now,
        });
        await MarkParticipantAsync(db, tenant.TenantId, question.QuestionId, command.Actor.UserId,
            participant => participant.Replied = true, cancellationToken);
        await PublishAsync(bus, tenant, new QuestionReplied(question.QuestionId, replyId, command.Actor.UserId, now), cancellationToken);
        return new ReplyToQuestionResult(question.QuestionId, replyId, question.Status);
    }

    public static async Task<QuestionChangeResult> Handle(EditQuestion command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        Required(command.Body, 8000, "question_body_required", "question_body_too_long", "Question body");
        var question = await FindAsync(db, tenant, command.QuestionId, cancellationToken);
        await EnsureVisibleAsync(question, command.Actor, records, cancellationToken);
        if (question.AskedBy != command.Actor.UserId)
            throw new QuestionRuleException("question_edit_author_only", "Only the question author may edit its body.");
        db.Revisions.Add(new QuestionReplyRevision
        {
            TenantId = tenant.TenantId, RevisionId = TimeOrderedId.New(clock.UtcNow), QuestionId = question.QuestionId,
            QuestionBody = true, Body = question.Body, RevisedBy = command.Actor.UserId, RevisedAt = clock.UtcNow,
        });
        question.Body = command.Body.Trim();
        return new QuestionChangeResult(question.QuestionId, question.Status);
    }

    public static async Task<QuestionChangeResult> Handle(EditReply command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        Required(command.Body, 8000, "reply_body_required", "reply_body_too_long", "Reply body");
        var question = await FindAsync(db, tenant, command.QuestionId, cancellationToken);
        await EnsureVisibleAsync(question, command.Actor, records, cancellationToken);
        var reply = await db.Replies.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == command.QuestionId && x.ReplyId == command.ReplyId, cancellationToken)
            ?? throw new QuestionRuleException("reply_not_found", "The reply does not exist.");
        if (reply.Author != command.Actor.UserId)
            throw new QuestionRuleException("reply_edit_author_only", "Only the reply author may edit it.");
        var now = clock.UtcNow;
        db.Revisions.Add(new QuestionReplyRevision
        {
            TenantId = tenant.TenantId, RevisionId = TimeOrderedId.New(now), QuestionId = command.QuestionId,
            ReplyId = reply.ReplyId, QuestionBody = false, Body = reply.Body,
            RevisedBy = command.Actor.UserId, RevisedAt = now,
        });
        reply.Body = command.Body.Trim();
        reply.EditedAt = now;
        return new QuestionChangeResult(command.QuestionId, question.Status);
    }

    public static async Task<QuestionChangeResult> Handle(AcceptQuestionAnswer command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantStaffDirectory staff, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var question = await FindAsync(db, tenant, command.QuestionId, cancellationToken);
        await EnsureVisibleAsync(question, command.Actor, records, cancellationToken);
        if (question.Status != QuestionStatus.Open)
            throw new QuestionRuleException("question_not_open", "Only an open question can accept an answer.");
        var allowed = question.AskedBy == command.Actor.UserId
            || command.Actor.Roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal)
            || (question.DirectedToRole is { } role && command.Actor.Roles.Contains(role, StringComparer.Ordinal))
            // A supervisor stands in only when the asker has left the tenant or been deactivated (owner answer, P13 vetting Q1).
            || (command.Actor.Roles.Contains(YojanaRole.ServiceSupervisor, StringComparer.Ordinal)
                && !await staff.ActiveStaffMemberExistsAsync(question.AskedBy, cancellationToken));
        if (!allowed) throw new QuestionRuleException("accept_answer_forbidden", "The current user may not accept this answer.");
        if (!await db.Replies.AnyAsync(x => x.TenantId == tenant.TenantId && x.QuestionId == question.QuestionId
            && x.ReplyId == command.ReplyId, cancellationToken))
            throw new QuestionRuleException("reply_not_found", "The reply does not belong to this question.");
        question.Status = QuestionStatus.Answered;
        question.AcceptedReplyId = command.ReplyId;
        question.ClosedReason = null;
        question.ClosedBy = null;
        question.ClosedAt = null;
        await PublishAsync(bus, tenant, new QuestionAnswered(question.QuestionId, command.ReplyId,
            command.Actor.UserId, clock.UtcNow), cancellationToken);
        return new QuestionChangeResult(question.QuestionId, question.Status);
    }

    public static async Task<QuestionChangeResult> Handle(CloseQuestionWithoutAnswer command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantContext tenant, IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        Required(command.Reason, 500, "close_reason_required", "close_reason_too_long", "Close reason");
        var question = await FindAsync(db, tenant, command.QuestionId, cancellationToken);
        await EnsureVisibleAsync(question, command.Actor, records, cancellationToken);
        EnsureAskerOrAdmin(question, command.Actor, "close_question_forbidden");
        if (question.Status != QuestionStatus.Open)
            throw new QuestionRuleException("question_not_open", "Only an open question can be closed without an answer.");
        var now = clock.UtcNow;
        question.Status = QuestionStatus.ClosedWithoutAnswer;
        question.AcceptedReplyId = null;
        question.ClosedReason = command.Reason.Trim();
        question.ClosedBy = command.Actor.UserId;
        question.ClosedAt = now;
        await PublishAsync(bus, tenant, new QuestionClosed(question.QuestionId, command.Actor.UserId, now,
            question.ClosedReason), cancellationToken);
        return new QuestionChangeResult(question.QuestionId, question.Status);
    }

    public static async Task<QuestionChangeResult> Handle(ReopenQuestion command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantContext tenant, IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        var question = await FindAsync(db, tenant, command.QuestionId, cancellationToken);
        await EnsureVisibleAsync(question, command.Actor, records, cancellationToken);
        EnsureAskerOrAdmin(question, command.Actor, "reopen_question_forbidden");
        if (question.Status == QuestionStatus.Open)
            throw new QuestionRuleException("question_already_open", "The question is already open.");
        question.Status = QuestionStatus.Open;
        question.AcceptedReplyId = null;
        question.ClosedReason = null;
        question.ClosedBy = null;
        question.ClosedAt = null;
        question.ReopenedCount++;
        await PublishAsync(bus, tenant, new QuestionReopened(question.QuestionId, command.Actor.UserId,
            clock.UtcNow), cancellationToken);
        return new QuestionChangeResult(question.QuestionId, question.Status);
    }

    private static async Task<Question> FindAsync(KnowledgeDbContext db, ITenantContext tenant, Guid questionId,
        CancellationToken cancellationToken) => await db.Questions.FirstOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.QuestionId == questionId, cancellationToken)
        ?? throw new QuestionRuleException("question_not_found", "The question does not exist.");

    private static async Task EnsureVisibleAsync(Question question, QuestionActor actor, RecordReferenceLookup records,
        CancellationToken cancellationToken)
    {
        var record = await records.OneAsync(question.RecordKind, question.RecordId,
            new RecordReferenceViewer(actor.UserId, actor.Roles), cancellationToken);
        if (!record.Exists) throw new QuestionRuleException("record_not_found", "The record does not exist.");
        if (!record.MayView) throw new QuestionRuleException("record_not_visible", "The current user may not view this record.");
    }

    private static void EnsureAskerOrAdmin(Question question, QuestionActor actor, string rule)
    {
        if (question.AskedBy != actor.UserId && !actor.Roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal))
            throw new QuestionRuleException(rule, "Only the asker or a tenant administrator may do this.");
    }

    private static void Required(string value, int max, string requiredRule, string maxRule, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new QuestionRuleException(requiredRule, $"{label} is required.");
        if (value.Trim().Length > max) throw new QuestionRuleException(maxRule, $"{label} is at most {max} characters.");
    }

    private static async Task MarkParticipantAsync(KnowledgeDbContext db, TenantId tenantId, Guid questionId,
        Guid userId, Action<QuestionParticipant> change, CancellationToken cancellationToken)
    {
        var participant = db.Participants.Local.FirstOrDefault(x => x.TenantId == tenantId
            && x.QuestionId == questionId && x.UserId == userId)
            ?? await db.Participants.FirstOrDefaultAsync(x => x.TenantId == tenantId
                && x.QuestionId == questionId && x.UserId == userId, cancellationToken);
        if (participant is null)
        {
            participant = new QuestionParticipant { TenantId = tenantId, QuestionId = questionId, UserId = userId };
            db.Participants.Add(participant);
        }
        change(participant);
    }

    private static ValueTask PublishAsync(IMessageBus bus, ITenantContext tenant, object message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return bus.PublishAsync(message, new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
    }
}
