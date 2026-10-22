using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Files;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Knowledge.Questions;

public sealed class QuestionRetentionSettings(ITenantSettings settings)
{
    public const string EnabledKey = "knowledge.questions.purge_closed.enabled";
    public const string YearsKey = "knowledge.questions.purge_closed.years";
    public const int DefaultYears = 7;

    public async Task<Policy> PolicyAsync(CancellationToken cancellationToken)
    {
        var enabled = bool.TryParse(await settings.GetAsync(EnabledKey, cancellationToken), out var parsed) && parsed;
        var rawYears = await settings.GetAsync(YearsKey, cancellationToken);
        var years = int.TryParse(rawYears, out var parsedYears) && parsedYears is >= 1 and <= 100
            ? parsedYears : DefaultYears;
        return new(enabled, years);
    }

    public sealed record Policy(bool Enabled, int Years);
}

public sealed record QuestionPurgeResult(bool Enabled, int Questions, int Replies, int Revisions,
    int Attachments, Guid? RunId);

public sealed class QuestionRetentionService(KnowledgeDbContext db, QuestionRetentionSettings settings,
    ITenantFileLifecycle files, ITenantContext tenant, IClock clock)
{
    public const int BatchSize = 500;

    public async Task<QuestionPurgeResult> RunBatchAsync(CancellationToken cancellationToken)
    {
        await DrainPendingFilesAsync(cancellationToken);
        var policy = await settings.PolicyAsync(cancellationToken);
        if (!policy.Enabled) return new(false, 0, 0, 0, 0, null);
        var started = clock.UtcNow;
        var cutoff = started.AddYears(-policy.Years);
        var ids = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && ((x.Status == QuestionStatus.Answered && x.AcceptedAt <= cutoff)
                    || (x.Status == QuestionStatus.ClosedWithoutAnswer && x.ClosedAt <= cutoff))
                && !db.Entries.Any(entry => entry.TenantId == x.TenantId
                    && entry.SourceQuestionId == x.QuestionId))
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.QuestionId).Select(x => x.QuestionId)
            .Take(BatchSize).ToListAsync(cancellationToken);
        var runId = TimeOrderedId.New(started);
        var replies = ids.Count == 0 ? 0 : await db.Replies.CountAsync(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.QuestionId), cancellationToken);
        var revisions = ids.Count == 0 ? 0 : await db.Revisions.CountAsync(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.QuestionId), cancellationToken);
        var attachmentIds = ids.Count == 0 ? [] : await db.Attachments.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
            .Select(x => x.FileId).ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (ids.Count > 0)
        {
            await db.Notifications.Where(x => x.TenantId == tenant.TenantId && x.QuestionId != null
                    && ids.Contains(x.QuestionId.Value)).ExecuteDeleteAsync(cancellationToken);
            await db.SimilarEntries.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Participants.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Attachments.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Revisions.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Replies.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Questions.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.QuestionId))
                .ExecuteDeleteAsync(cancellationToken);
            db.PendingFilePurges.AddRange(attachmentIds.Select(fileId => new QuestionFilePurgePending
            {
                TenantId = tenant.TenantId, FileId = fileId, QueuedAt = started, Reason = "retention",
            }));
        }
        db.PurgeRuns.Add(new QuestionPurgeRun
        {
            TenantId = tenant.TenantId, RunId = runId, TriggeredByKind = "system",
            TriggeredByLabel = QuestionRetentionJob.JobName, StartedAt = started, CompletedAt = clock.UtcNow,
            Cutoff = cutoff, RetentionYears = policy.Years, QuestionsPurged = ids.Count, RepliesPurged = replies,
            RevisionsPurged = revisions, AttachmentsPurged = attachmentIds.Count,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // COUPLING (cross-database recorded deletion): Knowledge commits this short-lived hand-off before Platform
        // deletes bytes. Reversing the order can leave a readable attachment row pointing at missing bytes; removing
        // the hand-off can leave personal data orphaned if the file database is temporarily unavailable.
        await DrainPendingFilesAsync(cancellationToken);
        return new(true, ids.Count, replies, revisions, attachmentIds.Count, runId);
    }

    public async Task DrainPendingFilesAsync(CancellationToken cancellationToken)
    {
        var pending = await db.PendingFilePurges.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .OrderBy(x => x.QueuedAt).ThenBy(x => x.FileId).Select(x => x.FileId).Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return;
        await files.PurgeAsync(pending, cancellationToken);
        await db.PendingFilePurges.Where(x => x.TenantId == tenant.TenantId && pending.Contains(x.FileId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}

public sealed class QuestionRetentionJob(ITenantContextSetter setter, QuestionRetentionService retention)
    : TenantJob(setter)
{
    public const string JobName = "knowledge-question-retention";
    public const string Cron = "0 40 2 * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken) => await retention.RunBatchAsync(cancellationToken);
}

public sealed record RedactQuestionPersonalData(Guid PersonId, Guid RequestedByUserId,
    IReadOnlyList<Guid> QuestionIds, IReadOnlyList<Guid> ReplyIds, IReadOnlyList<Guid> AttachmentIds,
    string Reason, QuestionActor Actor);

public sealed record QuestionRedactionResult(Guid RedactionId, int QuestionsRedacted, int RepliesRedacted,
    int RevisionsRedacted, int AttachmentsPurged);

public sealed class QuestionPrivacyService(KnowledgeDbContext db, ITenantFileLifecycle files,
    ITenantContext tenant, IClock clock)
{
    public const string NeutralAuthor = "Redacted person";
    public const string RedactedText = "[Personal data redacted]";

    public async Task<QuestionRedactionResult> RedactAsync(RedactQuestionPersonalData command,
        CancellationToken cancellationToken)
    {
        if (!command.Actor.Roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal))
            throw new QuestionRuleException("question_redaction_admin_required",
                "Only a tenant administrator may record a personal-data redaction.");
        Required(command.Reason, 1000, "question_redaction_reason_required", "question_redaction_reason_too_long",
            "Redaction reason");
        if (command.QuestionIds.Count + command.ReplyIds.Count + command.AttachmentIds.Count is 0 or > 500)
            throw new QuestionRuleException("question_redaction_scope_invalid",
                "Choose between 1 and 500 questions, replies or attachments.");
        var questionIds = command.QuestionIds.Distinct().ToList();
        var replyIds = command.ReplyIds.Distinct().ToList();
        var attachmentIds = command.AttachmentIds.Distinct().ToList();
        var questions = await db.Questions.Where(x => x.TenantId == tenant.TenantId
            && questionIds.Contains(x.QuestionId) && x.AskedBy == command.PersonId).ToListAsync(cancellationToken);
        var replies = await db.Replies.Where(x => x.TenantId == tenant.TenantId
            && replyIds.Contains(x.ReplyId) && x.Author == command.PersonId).ToListAsync(cancellationToken);
        var attachments = await db.Attachments.Where(x => x.TenantId == tenant.TenantId
            && attachmentIds.Contains(x.FileId)).ToListAsync(cancellationToken);
        if (attachments.Any(x => x.QuestionBody
                ? !questionIds.Contains(x.QuestionId)
                : x.ReplyId is null || !replyIds.Contains(x.ReplyId.Value)))
            throw new QuestionRuleException("question_redaction_attachment_outside_scope",
                "Each selected attachment must belong to a selected question or reply.");
        if (questions.Count != questionIds.Count || replies.Count != replyIds.Count ||
            attachments.Count != attachmentIds.Count)
            throw new QuestionRuleException("question_redaction_scope_not_found",
                "Part of the selected redaction scope is missing or was not authored by the selected person.");

        foreach (var question in questions)
        {
            question.AskedBy = Guid.Empty;
            question.Title = RedactedText;
            question.Body = RedactedText;
            question.UpdatedAt = clock.UtcNow;
        }
        foreach (var reply in replies)
        {
            reply.Author = Guid.Empty;
            reply.Body = RedactedText;
            reply.EditedAt = clock.UtcNow;
        }
        var revisions = await db.Revisions.Where(x => x.TenantId == tenant.TenantId
            && ((x.QuestionBody && questionIds.Contains(x.QuestionId))
                || (x.ReplyId != null && replyIds.Contains(x.ReplyId.Value)))).ToListAsync(cancellationToken);
        foreach (var revision in revisions)
        {
            revision.RevisedBy = Guid.Empty;
            revision.Body = RedactedText;
        }
        db.Attachments.RemoveRange(attachments);
        var now = clock.UtcNow;
        db.PendingFilePurges.AddRange(attachments.Select(x => new QuestionFilePurgePending
        {
            TenantId = tenant.TenantId, FileId = x.FileId, QueuedAt = now, Reason = "redaction",
        }));
        var redactionId = TimeOrderedId.New(now);
        db.PersonalDataRedactions.Add(new QuestionPersonalDataRedaction
        {
            TenantId = tenant.TenantId, RedactionId = redactionId, PersonId = command.PersonId,
            RequestedByUserId = command.RequestedByUserId, PerformedByUserId = command.Actor.UserId,
            PerformedAt = now, ScopeJson = JsonSerializer.Serialize(new
            {
                questionIds, replyIds, attachmentIds,
            }), Reason = command.Reason.Trim(), QuestionsRedacted = questions.Count,
            RepliesRedacted = replies.Count, RevisionsRedacted = revisions.Count,
            AttachmentsPurged = attachments.Count,
        });
        await db.SaveChangesAsync(cancellationToken);
        // Coupled to QuestionRetentionService's pending-file protocol; both redaction and retention must drain the same
        // queue or a file-store outage can leave bytes behind after the visible thread has been redacted.
        await DrainAsync(cancellationToken);
        return new(redactionId, questions.Count, replies.Count, revisions.Count, attachments.Count);
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportAsync(Guid personId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var questions = db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.AskedBy == personId).OrderBy(x => x.AskedAt).ThenBy(x => x.QuestionId)
            .Select(x => new { x.AskedAt, x.RecordKind, x.Status, x.Title, x.Body });
        await foreach (var question in questions.AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return ["question", question.AskedAt.ToString("O"), question.RecordKind, question.Status,
                question.Title, question.Body, null];
        var replies = db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.Author == personId).OrderBy(x => x.At).ThenBy(x => x.ReplyId)
            .Join(db.Questions.AsNoTracking(), reply => new { reply.TenantId, reply.QuestionId },
                question => new { question.TenantId, question.QuestionId },
                (reply, question) => new { reply.At, question.RecordKind, question.Status,
                    QuestionTitle = question.Title, reply.Body, reply.EditedAt });
        await foreach (var reply in replies.AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return ["reply", reply.At.ToString("O"), reply.RecordKind, reply.Status,
                reply.QuestionTitle, reply.Body, reply.EditedAt?.ToString("O")];
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var ids = await db.PendingFilePurges.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .OrderBy(x => x.QueuedAt).ThenBy(x => x.FileId).Select(x => x.FileId).Take(500)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0) return;
        await files.PurgeAsync(ids, cancellationToken);
        await db.PendingFilePurges.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.FileId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static void Required(string value, int maximum, string missingRule, string longRule, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new QuestionRuleException(missingRule, $"{label} is required.");
        if (value.Trim().Length > maximum) throw new QuestionRuleException(longRule,
            $"{label} is at most {maximum} characters.");
    }
}
