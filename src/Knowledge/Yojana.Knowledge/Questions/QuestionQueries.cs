using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Data;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Questions;

public sealed record QuestionReplyView(Guid ReplyId, string Body, Guid AuthorId, string AuthorName,
    DateTime At, DateTime? EditedAt, int RevisionCount);

public sealed record QuestionThreadView(Guid QuestionId, string RecordKind, Guid RecordId, string RecordName,
    string Title, string Body, string Status, Guid? DirectedToUser, string? DirectedToUserName,
    string? DirectedToRole, string? DirectedToRoleLabel, Guid AskedBy, string AskedByName, DateTime AskedAt,
    Guid? AcceptedReplyId, string? ClosedReason, Guid? ClosedBy, string? ClosedByName, DateTime? ClosedAt,
    int ReopenedCount, int QuestionRevisionCount, IReadOnlyList<QuestionReplyView> Replies);

public sealed record QuestionListItem(Guid QuestionId, string Title, string Status, Guid AskedBy,
    string AskedByName, DateTime AskedAt, Guid? DirectedToUser, string? DirectedToUserName,
    string? DirectedToRole, string? DirectedToRoleLabel, int ReplyCount);

public sealed record QuestionStatusCounts(int Open, int Answered, int ClosedWithoutAnswer, int Total);

public sealed record QuestionPage(string RecordKind, Guid RecordId, string RecordName,
    IReadOnlyList<QuestionListItem> Items, string? NextCursor, QuestionStatusCounts Counts);

public sealed record QuestionRevisionView(Guid RevisionId, string Body, Guid RevisedBy,
    string RevisedByName, DateTime RevisedAt);

public sealed class QuestionQueries(KnowledgeDbContext db, RecordReferenceLookup records,
    IUserDirectory users, ITenantContext tenant)
{
    public async Task<QuestionThreadView?> ThreadAsync(Guid questionId, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId, cancellationToken);
        if (question is null) return null;
        var record = await records.OneAsync(question.RecordKind, question.RecordId, viewer, cancellationToken);
        EnsureVisible(record);
        var replies = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.QuestionId == questionId).OrderBy(x => x.At).ThenBy(x => x.ReplyId).ToListAsync(cancellationToken);
        var revisionRows = await db.Revisions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId).Select(x => new { x.ReplyId, x.RevisedBy }).ToListAsync(cancellationToken);
        var refs = replies.Select(x => x.Author).Append(question.AskedBy)
            .Concat(question.DirectedToUser is { } directed ? [directed] : [])
            .Concat(question.ClosedBy is { } closed ? [closed] : [])
            .Concat(revisionRows.Select(x => x.RevisedBy)).Distinct().Select(x => x.ToString("D")).ToList();
        var names = await users.DisplayNamesAsync(refs, cancellationToken);
        string Name(Guid id) => names.GetValueOrDefault(id.ToString("D"), "Former staff member");
        return new QuestionThreadView(question.QuestionId, question.RecordKind, question.RecordId,
            record.DisplayName!, question.Title, question.Body, question.Status, question.DirectedToUser,
            question.DirectedToUser is { } directedUser ? Name(directedUser) : null, question.DirectedToRole,
            question.DirectedToRole is { } role ? RoleLabel(role) : null, question.AskedBy, Name(question.AskedBy),
            question.AskedAt, question.AcceptedReplyId, question.ClosedReason, question.ClosedBy,
            question.ClosedBy is { } closedBy ? Name(closedBy) : null, question.ClosedAt, question.ReopenedCount,
            revisionRows.Count(x => x.ReplyId == null), replies.Select(reply => new QuestionReplyView(reply.ReplyId,
                reply.Body, reply.Author, Name(reply.Author), reply.At, reply.EditedAt,
                revisionRows.Count(x => x.ReplyId == reply.ReplyId))).ToList());
    }

    public async Task<QuestionPage> OnRecordAsync(string recordKind, Guid recordId, string? status,
        string? cursor, int take, RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        if (status is not null && !Knowledge.Contracts.QuestionStatus.All.Contains(status, StringComparer.Ordinal))
            throw new QuestionRuleException("question_status_unknown", $"'{status}' is not a question status.");
        if (take is < 1 or > 100) throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
        var record = await records.OneAsync(recordKind, recordId, viewer, cancellationToken);
        EnsureVisible(record);
        var query = db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.RecordKind == recordKind && x.RecordId == recordId);
        var grouped = await query.GroupBy(x => x.Status).Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var (at, id) = Decode(cursor);
            query = query.Where(x => x.AskedAt < at || (x.AskedAt == at && x.QuestionId.CompareTo(id) < 0));
        }
        var rows = await query.OrderByDescending(x => x.AskedAt).ThenByDescending(x => x.QuestionId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var ids = page.Select(x => x.QuestionId).ToList();
        var replyCounts = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && ids.Contains(x.QuestionId)).GroupBy(x => x.QuestionId)
            .Select(group => new { QuestionId = group.Key, Count = group.Count() }).ToDictionaryAsync(x => x.QuestionId,
                x => x.Count, cancellationToken);
        var refs = page.Select(x => x.AskedBy).Concat(page.Where(x => x.DirectedToUser != null)
            .Select(x => x.DirectedToUser!.Value)).Distinct().Select(x => x.ToString("D")).ToList();
        var names = await users.DisplayNamesAsync(refs, cancellationToken);
        string Name(Guid id) => names.GetValueOrDefault(id.ToString("D"), "Former staff member");
        var next = rows.Count > take && page.Count > 0 ? Encode(page[^1].AskedAt, page[^1].QuestionId) : null;
        int Count(string value) => grouped.FirstOrDefault(x => x.Status == value)?.Count ?? 0;
        var counts = new QuestionStatusCounts(Count(Knowledge.Contracts.QuestionStatus.Open),
            Count(Knowledge.Contracts.QuestionStatus.Answered), Count(Knowledge.Contracts.QuestionStatus.ClosedWithoutAnswer),
            grouped.Sum(x => x.Count));
        return new QuestionPage(recordKind, recordId, record.DisplayName!, page.Select(x => new QuestionListItem(
            x.QuestionId, x.Title, x.Status, x.AskedBy, Name(x.AskedBy), x.AskedAt, x.DirectedToUser,
            x.DirectedToUser is { } directed ? Name(directed) : null, x.DirectedToRole,
            x.DirectedToRole is { } role ? RoleLabel(role) : null, replyCounts.GetValueOrDefault(x.QuestionId))).ToList(),
            next, counts);
    }

    public async Task<IReadOnlyList<QuestionRevisionView>?> RevisionsAsync(Guid questionId, Guid? replyId,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId, cancellationToken);
        if (question is null) return null;
        var record = await records.OneAsync(question.RecordKind, question.RecordId, viewer, cancellationToken);
        EnsureVisible(record);
        if (replyId is { } expected && !await db.Replies.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId && x.ReplyId == expected, cancellationToken)) return null;
        var rows = await db.Revisions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.QuestionId == questionId && x.ReplyId == replyId && x.QuestionBody == (replyId == null))
            .OrderByDescending(x => x.RevisedAt).ThenByDescending(x => x.RevisionId).ToListAsync(cancellationToken);
        var names = await users.DisplayNamesAsync(rows.Select(x => x.RevisedBy.ToString("D")).Distinct().ToList(),
            cancellationToken);
        return rows.Select(x => new QuestionRevisionView(x.RevisionId, x.Body, x.RevisedBy,
            names.GetValueOrDefault(x.RevisedBy.ToString("D"), "Former staff member"), x.RevisedAt)).ToList();
    }

    private static void EnsureVisible(RecordReference record)
    {
        if (!record.Exists) throw new QuestionRuleException("record_not_found", "The record does not exist.");
        if (!record.MayView) throw new QuestionRuleException("record_not_visible", "The current user may not view this record.");
        if (string.IsNullOrWhiteSpace(record.DisplayName))
            throw new InvalidOperationException("A visible record reference must have a readable display name.");
    }

    private static string RoleLabel(string role) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(role.Replace('_', ' '));

    private static string Encode(DateTime at, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.Ticks}:{id:D}"));

    private static (DateTime At, Guid Id) Decode(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return (new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new QuestionRuleException("cursor_invalid", "The question cursor is invalid.");
        }
    }
}
