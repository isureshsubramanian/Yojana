using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Files;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Knowledge.Privacy;

public sealed class KnowledgePersonalDataHolder(
    KnowledgeDbContext db,
    ITenantContext tenant,
    IClock clock,
    ITenantFileLifecycle files) : IPersonalDataHolder
{
    private const int BatchSize = 500;
    private const string Redacted = "[Personal data redacted]";
    public string Store => "knowledge";

    public async Task<PersonalDataPreview> PreviewAsync(ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        var review = subject.Kind == ErasureSubjectKind.User
            ? await UserReviewItemsAsync(subject.SubjectId, cancellationToken) : [];
        return new([
            new("questions", await CountQuestionMatchesAsync(subject, cancellationToken),
                "authorship removed, exact values replaced and selected fields redacted"),
            new("knowledge_entries", await CountEntryMatchesAsync(subject, cancellationToken),
                "authorship removed, including every stored entry version"),
        ], review, []);
    }

    public async Task<PersonalDataEraseResult> EraseAsync(ErasureSubject subject, Guid erasureId,
        CancellationToken cancellationToken)
    {
        var choices = subject.ReviewDecisions.ToDictionary(x => x.ItemKey, x => x.Action, StringComparer.Ordinal);
        var questionChanges = await EraseQuestionsAsync(subject, choices, cancellationToken)
            + await EraseRepliesAsync(subject, choices, cancellationToken)
            + await EraseRevisionsAsync(subject, choices, cancellationToken);
        var entryChanges = await EraseEntriesAsync(subject, choices, cancellationToken)
            + await EraseEntryVersionsAsync(subject, choices, cancellationToken);
        return new([
            new("questions", questionChanges,
                "authorship removed, exact values replaced and selected fields redacted"),
            new("knowledge_entries", entryChanges,
                "authorship removed, including every stored entry version"),
        ]);
    }

    private async Task<int> EraseQuestionsAsync(ErasureSubject subject,
        IReadOnlyDictionary<string, PersonalDataReviewAction> choices, CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursor = Guid.Empty;
        var predicate = QuestionPredicate(subject);
        while (true)
        {
            var rows = await db.Questions.Where(x => x.TenantId == tenant.TenantId
                    && x.QuestionId.CompareTo(cursor) > 0).Where(predicate)
                .OrderBy(x => x.QuestionId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            var attachmentQuestions = new HashSet<Guid>();
            foreach (var row in rows)
            {
                var linked = subject.Kind == ErasureSubjectKind.User && row.AskedBy == subject.SubjectId;
                row.Title = Change(row.Title, $"question:{row.QuestionId:N}:title", linked, subject, choices,
                    out var titleChanged);
                row.Body = Change(row.Body, $"question:{row.QuestionId:N}:body", linked, subject, choices,
                    out var bodyChanged);
                if (linked)
                {
                    row.AskedBy = Guid.Empty;
                    if (row.ClosedBy == subject.SubjectId) row.ClosedBy = Guid.Empty;
                    row.UpdatedAt = clock.UtcNow;
                    attachmentQuestions.Add(row.QuestionId);
                }
                if (linked || titleChanged + bodyChanged > 0) changed++;
            }
            cursor = rows[^1].QuestionId;
            await db.SaveChangesAsync(cancellationToken);
            await PurgeAttachmentsAsync(attachmentQuestions, cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> EraseRepliesAsync(ErasureSubject subject,
        IReadOnlyDictionary<string, PersonalDataReviewAction> choices, CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursor = Guid.Empty;
        var predicate = ReplyPredicate(subject);
        while (true)
        {
            var rows = await db.Replies.Where(x => x.TenantId == tenant.TenantId
                    && x.ReplyId.CompareTo(cursor) > 0).Where(predicate)
                .OrderBy(x => x.ReplyId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            var attachmentQuestions = new HashSet<Guid>();
            foreach (var row in rows)
            {
                var linked = subject.Kind == ErasureSubjectKind.User && row.Author == subject.SubjectId;
                row.Body = Change(row.Body, $"reply:{row.ReplyId:N}:body", linked, subject, choices,
                    out var textChanged);
                if (linked)
                {
                    row.Author = Guid.Empty;
                    row.EditedAt = clock.UtcNow;
                    attachmentQuestions.Add(row.QuestionId);
                }
                if (linked || textChanged > 0) changed++;
            }
            cursor = rows[^1].ReplyId;
            await db.SaveChangesAsync(cancellationToken);
            await PurgeAttachmentsAsync(attachmentQuestions, cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> EraseRevisionsAsync(ErasureSubject subject,
        IReadOnlyDictionary<string, PersonalDataReviewAction> choices, CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursor = Guid.Empty;
        var predicate = RevisionPredicate(subject);
        while (true)
        {
            var rows = await db.Revisions.Where(x => x.TenantId == tenant.TenantId
                    && x.RevisionId.CompareTo(cursor) > 0).Where(predicate)
                .OrderBy(x => x.RevisionId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            var attachmentQuestions = new HashSet<Guid>();
            foreach (var row in rows)
            {
                var linked = subject.Kind == ErasureSubjectKind.User && row.RevisedBy == subject.SubjectId;
                row.Body = Change(row.Body, $"revision:{row.RevisionId:N}:body", linked, subject, choices,
                    out var textChanged);
                if (linked)
                {
                    row.RevisedBy = Guid.Empty;
                    attachmentQuestions.Add(row.QuestionId);
                }
                if (linked || textChanged > 0) changed++;
            }
            cursor = rows[^1].RevisionId;
            await db.SaveChangesAsync(cancellationToken);
            await PurgeAttachmentsAsync(attachmentQuestions, cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> EraseEntriesAsync(ErasureSubject subject,
        IReadOnlyDictionary<string, PersonalDataReviewAction> choices, CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursor = Guid.Empty;
        var predicate = EntryPredicate(subject);
        while (true)
        {
            var rows = await db.Entries.Where(x => x.TenantId == tenant.TenantId
                    && x.EntryId.CompareTo(cursor) > 0).Where(predicate)
                .OrderBy(x => x.EntryId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var linked = subject.Kind == ErasureSubjectKind.User && (row.AuthorId == subject.SubjectId
                    || row.LastSavedBy == subject.SubjectId || row.PublishedBy == subject.SubjectId);
                row.Title = Change(row.Title, $"entry:{row.EntryId:N}:title", linked, subject, choices,
                    out var titleChanged);
                row.Problem = Change(row.Problem, $"entry:{row.EntryId:N}:problem", linked, subject, choices,
                    out var problemChanged);
                row.Answer = Change(row.Answer, $"entry:{row.EntryId:N}:answer", linked, subject, choices,
                    out var answerChanged);
                row.Steps = Change(row.Steps, $"entry:{row.EntryId:N}:steps", linked, subject, choices,
                    out var stepsChanged);
                if (row.AuthorId == subject.SubjectId) row.AuthorId = Guid.Empty;
                if (row.LastSavedBy == subject.SubjectId) row.LastSavedBy = Guid.Empty;
                if (row.PublishedBy == subject.SubjectId) row.PublishedBy = Guid.Empty;
                if (linked || titleChanged + problemChanged + answerChanged + stepsChanged > 0) changed++;
            }
            cursor = rows[^1].EntryId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> EraseEntryVersionsAsync(ErasureSubject subject,
        IReadOnlyDictionary<string, PersonalDataReviewAction> choices, CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursorId = Guid.Empty;
        var cursorVersion = -1;
        var predicate = EntryVersionPredicate(subject);
        while (true)
        {
            var rows = await db.EntryVersions.Where(x => x.TenantId == tenant.TenantId
                    && (x.EntryId.CompareTo(cursorId) > 0
                        || x.EntryId == cursorId && x.Version > cursorVersion)).Where(predicate)
                .OrderBy(x => x.EntryId).ThenBy(x => x.Version).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var linked = subject.Kind == ErasureSubjectKind.User && row.SavedBy == subject.SubjectId;
                var prefix = $"entry-version:{row.EntryId:N}:{row.Version}";
                row.Title = Change(row.Title, $"{prefix}:title", linked, subject, choices, out var titleChanged);
                row.Problem = Change(row.Problem, $"{prefix}:problem", linked, subject, choices,
                    out var problemChanged);
                row.Answer = Change(row.Answer, $"{prefix}:answer", linked, subject, choices,
                    out var answerChanged);
                row.Steps = Change(row.Steps, $"{prefix}:steps", linked, subject, choices,
                    out var stepsChanged);
                if (linked) row.SavedBy = Guid.Empty;
                if (linked || titleChanged + problemChanged + answerChanged + stepsChanged > 0) changed++;
            }
            cursorId = rows[^1].EntryId;
            cursorVersion = rows[^1].Version;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task PurgeAttachmentsAsync(HashSet<Guid> questionIds,
        CancellationToken cancellationToken)
    {
        if (questionIds.Count == 0) return;
        var cursor = Guid.Empty;
        while (true)
        {
            var rows = await db.Attachments.Where(x => x.TenantId == tenant.TenantId
                    && questionIds.Contains(x.QuestionId) && x.FileId.CompareTo(cursor) > 0)
                .OrderBy(x => x.FileId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            var ids = rows.Select(x => x.FileId).ToList();
            cursor = ids[^1];
            db.Attachments.RemoveRange(rows);
            db.PendingFilePurges.AddRange(ids.Select(id => new QuestionFilePurgePending
            {
                TenantId = tenant.TenantId, FileId = id, QueuedAt = clock.UtcNow,
                Reason = "personal_data_erasure",
            }));
            await db.SaveChangesAsync(cancellationToken);
            await files.PurgeAsync(ids, cancellationToken);
            await db.PendingFilePurges.Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.FileId))
                .ExecuteDeleteAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    private async Task<List<PersonalDataReviewItem>> UserReviewItemsAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        var result = new List<PersonalDataReviewItem>();
        var questionCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.AskedBy == userId && x.QuestionId.CompareTo(questionCursor) > 0)
                .OrderBy(x => x.QuestionId).Take(BatchSize)
                .Select(x => new { x.QuestionId, x.Title, x.Body }).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                Add($"question:{row.QuestionId:N}:title", "question", row.QuestionId, "title", row.Title);
                Add($"question:{row.QuestionId:N}:body", "question", row.QuestionId, "body", row.Body);
            }
            questionCursor = rows[^1].QuestionId;
        }
        var replyCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.Author == userId && x.ReplyId.CompareTo(replyCursor) > 0)
                .OrderBy(x => x.ReplyId).Take(BatchSize).Select(x => new { x.ReplyId, x.Body })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
                Add($"reply:{row.ReplyId:N}:body", "question_reply", row.ReplyId, "body", row.Body);
            replyCursor = rows[^1].ReplyId;
        }
        var revisionCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.Revisions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.RevisedBy == userId && x.RevisionId.CompareTo(revisionCursor) > 0)
                .OrderBy(x => x.RevisionId).Take(BatchSize).Select(x => new { x.RevisionId, x.Body })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
                Add($"revision:{row.RevisionId:N}:body", "question_revision", row.RevisionId, "body", row.Body);
            revisionCursor = rows[^1].RevisionId;
        }
        var entryCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && (x.AuthorId == userId || x.LastSavedBy == userId || x.PublishedBy == userId)
                    && x.EntryId.CompareTo(entryCursor) > 0).OrderBy(x => x.EntryId).Take(BatchSize)
                .Select(x => new { x.EntryId, x.Title, x.Problem, x.Answer, x.Steps })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                Add($"entry:{row.EntryId:N}:title", "knowledge_entry", row.EntryId, "title", row.Title);
                Add($"entry:{row.EntryId:N}:problem", "knowledge_entry", row.EntryId, "problem", row.Problem);
                Add($"entry:{row.EntryId:N}:answer", "knowledge_entry", row.EntryId, "answer", row.Answer);
                Add($"entry:{row.EntryId:N}:steps", "knowledge_entry", row.EntryId, "steps", row.Steps);
            }
            entryCursor = rows[^1].EntryId;
        }
        var versionCursorId = Guid.Empty;
        var versionCursor = -1;
        while (true)
        {
            var rows = await db.EntryVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.SavedBy == userId && (x.EntryId.CompareTo(versionCursorId) > 0
                        || x.EntryId == versionCursorId && x.Version > versionCursor))
                .OrderBy(x => x.EntryId).ThenBy(x => x.Version).Take(BatchSize)
                .Select(x => new { x.EntryId, x.Version, x.Title, x.Problem, x.Answer, x.Steps })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var prefix = $"entry-version:{row.EntryId:N}:{row.Version}";
                Add($"{prefix}:title", "knowledge_entry_version", row.EntryId, "title", row.Title);
                Add($"{prefix}:problem", "knowledge_entry_version", row.EntryId, "problem", row.Problem);
                Add($"{prefix}:answer", "knowledge_entry_version", row.EntryId, "answer", row.Answer);
                Add($"{prefix}:steps", "knowledge_entry_version", row.EntryId, "steps", row.Steps);
            }
            versionCursorId = rows[^1].EntryId;
            versionCursor = rows[^1].Version;
        }
        return result;

        void Add(string key, string store, Guid reference, string field, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) result.Add(new(key, store, reference.ToString("N"), field,
                value.Length <= 160 ? value : value[..160]));
        }
    }

    private async Task<int> CountQuestionMatchesAsync(ErasureSubject subject, CancellationToken cancellationToken) =>
        await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .Where(QuestionPredicate(subject)).CountAsync(cancellationToken)
        + await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .Where(ReplyPredicate(subject)).CountAsync(cancellationToken)
        + await db.Revisions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .Where(RevisionPredicate(subject)).CountAsync(cancellationToken);

    private async Task<int> CountEntryMatchesAsync(ErasureSubject subject, CancellationToken cancellationToken) =>
        await db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .Where(EntryPredicate(subject)).CountAsync(cancellationToken)
        + await db.EntryVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId)
            .Where(EntryVersionPredicate(subject)).CountAsync(cancellationToken);

    private static Expression<Func<Question, bool>> QuestionPredicate(ErasureSubject subject)
    {
        var text = PersonalDataQuery.ContainsAny<Question>(subject.KnownValues, x => x.Title, x => x.Body);
        return subject.Kind == ErasureSubjectKind.User
            ? PersonalDataQuery.Or<Question>(x => x.AskedBy == subject.SubjectId, text) : text;
    }

    private static Expression<Func<QuestionReply, bool>> ReplyPredicate(ErasureSubject subject)
    {
        var text = PersonalDataQuery.ContainsAny<QuestionReply>(subject.KnownValues, x => x.Body);
        return subject.Kind == ErasureSubjectKind.User
            ? PersonalDataQuery.Or<QuestionReply>(x => x.Author == subject.SubjectId, text) : text;
    }

    private static Expression<Func<QuestionReplyRevision, bool>> RevisionPredicate(ErasureSubject subject)
    {
        var text = PersonalDataQuery.ContainsAny<QuestionReplyRevision>(subject.KnownValues, x => x.Body);
        return subject.Kind == ErasureSubjectKind.User
            ? PersonalDataQuery.Or<QuestionReplyRevision>(x => x.RevisedBy == subject.SubjectId, text) : text;
    }

    private static Expression<Func<KnowledgeEntry, bool>> EntryPredicate(ErasureSubject subject)
    {
        var text = PersonalDataQuery.ContainsAny<KnowledgeEntry>(subject.KnownValues,
            x => x.Title, x => x.Problem, x => x.Answer, x => x.Steps);
        return subject.Kind == ErasureSubjectKind.User
            ? PersonalDataQuery.Or<KnowledgeEntry>(x => x.AuthorId == subject.SubjectId
                || x.LastSavedBy == subject.SubjectId || x.PublishedBy == subject.SubjectId, text) : text;
    }

    private static Expression<Func<KnowledgeEntryVersion, bool>> EntryVersionPredicate(ErasureSubject subject)
    {
        var text = PersonalDataQuery.ContainsAny<KnowledgeEntryVersion>(subject.KnownValues,
            x => x.Title, x => x.Problem, x => x.Answer, x => x.Steps);
        return subject.Kind == ErasureSubjectKind.User
            ? PersonalDataQuery.Or<KnowledgeEntryVersion>(x => x.SavedBy == subject.SubjectId, text) : text;
    }

    private static string Change(string value, string key, bool linked, ErasureSubject subject,
        IReadOnlyDictionary<string, PersonalDataReviewAction> choices, out int changed)
    {
        var before = value;
        if (linked && choices.GetValueOrDefault(key) == PersonalDataReviewAction.Redact) value = Redacted;
        else foreach (var known in subject.KnownValues.Where(x => !string.IsNullOrWhiteSpace(x))
                     .OrderByDescending(x => x.Length))
            value = value.Replace(known, subject.Token, StringComparison.Ordinal);
        changed = string.Equals(before, value, StringComparison.Ordinal) ? 0 : 1;
        return value;
    }
}
