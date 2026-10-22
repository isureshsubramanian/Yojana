using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Knowledge.Questions;

public sealed record QuestionReplyView(Guid ReplyId, string Body, Guid AuthorId, string AuthorName,
    DateTime At, DateTime? EditedAt, int RevisionCount, bool CanEdit);

public sealed record QuestionAllowedActions(bool CanEditQuestion, bool CanReply, bool CanAcceptAnswer,
    bool CanCloseWithoutAnswer, bool CanReopen);

public sealed record QuestionThreadView(Guid QuestionId, string RecordKind, Guid RecordId, string RecordName,
    string Title, string Body, string Status, Guid? DirectedToUser, string? DirectedToUserName,
    string? DirectedToRole, string? DirectedToRoleLabel, Guid AskedBy, string AskedByName, DateTime AskedAt,
    Guid? AcceptedReplyId, string? ClosedReason, Guid? ClosedBy, string? ClosedByName, DateTime? ClosedAt,
    int ReopenedCount, int QuestionRevisionCount, QuestionAllowedActions AllowedActions,
    IReadOnlyList<QuestionReplyView> Replies);

public sealed record QuestionListItem(Guid QuestionId, string Title, string Status, Guid AskedBy,
    string AskedByName, DateTime AskedAt, Guid? DirectedToUser, string? DirectedToUserName,
    string? DirectedToRole, string? DirectedToRoleLabel, int ReplyCount);

public sealed record QuestionStatusCounts(int Open, int Answered, int ClosedWithoutAnswer, int Total);

public sealed record QuestionPage(string RecordKind, Guid RecordId, string RecordName,
    IReadOnlyList<QuestionListItem> Items, string? NextCursor, QuestionStatusCounts Counts);

public sealed record QuestionRevisionView(Guid RevisionId, string Body, Guid RevisedBy,
    string RevisedByName, DateTime RevisedAt);

public sealed record QuestionAddresseePage(TenantStaffChoicePage People, IReadOnlyList<TenantRoleChoice> Roles);

public sealed record MyQuestionItem(Guid QuestionId, string Title, string Status, string RecordKind,
    Guid RecordId, string RecordName, string AskedByName, string DirectedTo, DateTime AskedAt,
    DateTime LastActivityAt, int ReplyCount, int WaitingDays);

public sealed record RankedQuestionCount(string Key, string Label, int Count);

public sealed record MyQuestionSummary(IReadOnlyList<RankedQuestionCount> WaitingByAge,
    IReadOnlyList<RankedQuestionCount> WaitingByRecordKind);

public sealed record MyQuestionPage(IReadOnlyList<MyQuestionItem> Items, string? NextCursor,
    MyQuestionSummary Summary);

public sealed record QuestionSearchItem(Guid QuestionId, string Title, string Status, string RecordKind,
    Guid RecordId, string RecordName, string Answer, int Rank, DateTime AskedAt);

public sealed record QuestionSearchPage(IReadOnlyList<object> ReviewedEntries,
    IReadOnlyList<QuestionSearchItem> Questions, string? NextCursor);

public sealed class QuestionQueries(KnowledgeDbContext db, RecordReferenceLookup records,
    IUserDirectory users, ITenantStaffDirectory staff, IQuestionTextSearch textSearch,
    QuestionNotificationService notifications, ITenantContext tenant, IClock clock)
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
        var askerActive = await staff.ActiveStaffMemberExistsAsync(question.AskedBy, cancellationToken);
        var isAsker = viewer.UserId == question.AskedBy;
        var administrator = viewer.Roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal);
        var open = question.Status == Knowledge.Contracts.QuestionStatus.Open;
        var canAccept = open && (isAsker || administrator ||
            (question.DirectedToRole is { } role && viewer.Roles.Contains(role, StringComparer.Ordinal)) ||
            (!askerActive && viewer.Roles.Contains(YojanaRole.ServiceSupervisor, StringComparer.Ordinal)));
        var allowed = new QuestionAllowedActions(isAsker, open, canAccept, open && (isAsker || administrator),
            !open && (isAsker || administrator));
        var view = new QuestionThreadView(question.QuestionId, question.RecordKind, question.RecordId,
            record.DisplayName!, question.Title, question.Body, question.Status, question.DirectedToUser,
            question.DirectedToUser is { } directedUser ? Name(directedUser) : null, question.DirectedToRole,
            question.DirectedToRole is { } directedRole ? RoleLabel(directedRole) : null, question.AskedBy, Name(question.AskedBy),
            question.AskedAt, question.AcceptedReplyId, question.ClosedReason, question.ClosedBy,
            question.ClosedBy is { } closedBy ? Name(closedBy) : null, question.ClosedAt, question.ReopenedCount,
            revisionRows.Count(x => x.ReplyId == null), allowed, replies.Select(reply => new QuestionReplyView(reply.ReplyId,
                reply.Body, reply.Author, Name(reply.Author), reply.At, reply.EditedAt,
                revisionRows.Count(x => x.ReplyId == reply.ReplyId), reply.Author == viewer.UserId)).ToList());
        await notifications.MarkReadAsync(questionId, viewer.UserId, cancellationToken);
        return view;
    }

    public async Task<QuestionPage> OnRecordAsync(string recordKind, Guid recordId, string? status,
        string? cursor, int take, RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        ValidateStatusAndTake(status, take);
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
        var replyCounts = await ReplyCountsAsync(page.Select(x => x.QuestionId).ToList(), cancellationToken);
        var names = await NamesAsync(page, cancellationToken);
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

    public async Task<QuestionAddresseePage> AddresseesAsync(string? search, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
        try
        {
            return new QuestionAddresseePage(await staff.ActiveStaffAsync(search, cursor, take, cancellationToken),
                await staff.ActiveRolesAsync(cancellationToken));
        }
        catch (ArgumentException exception)
        {
            throw new QuestionRuleException("cursor_invalid", exception.Message);
        }
    }

    public async Task<MyQuestionPage> MyAsync(string? scope, string? status, string? recordKind, string? age,
        string? search, string? cursor, int take, RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        ValidateStatusAndTake(status, take);
        scope ??= "all";
        if (scope is not ("all" or "asked" or "directed_to_me" or "directed_to_roles"))
            throw new QuestionRuleException("question_scope_unknown", $"'{scope}' is not a question scope.");
        if (recordKind is not null && !RecordKind.All.Contains(recordKind, StringComparer.Ordinal))
            throw new QuestionRuleException("record_kind_unknown", $"'{recordKind}' is not a question record kind.");
        var ageRange = AgeRange(age);
        var query = RelevantQuestions(viewer);
        query = scope switch
        {
            "asked" => query.Where(x => x.AskedBy == viewer.UserId),
            "directed_to_me" => query.Where(x => x.DirectedToUser == viewer.UserId),
            "directed_to_roles" => query.Where(x => x.DirectedToRole != null && viewer.Roles.Contains(x.DirectedToRole)),
            _ => query,
        };
        if (status is not null) query = query.Where(x => x.Status == status);
        if (recordKind is not null) query = query.Where(x => x.RecordKind == recordKind);
        if (ageRange.From is { } from) query = query.Where(x => x.AskedAt <= clock.UtcNow.AddDays(-from));
        if (ageRange.To is { } to) query = query.Where(x => x.AskedAt > clock.UtcNow.AddDays(-to));
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var (at, id) = Decode(cursor);
            query = query.Where(x => x.AskedAt > at || (x.AskedAt == at && x.QuestionId.CompareTo(id) > 0));
        }

        var visible = new List<(Question Question, RecordReference Record)>();
        DateTime? scanAt = null; Guid? scanId = null;
        while (visible.Count < take + 1)
        {
            var batchQuery = query;
            if (scanAt is { } at && scanId is { } id)
                batchQuery = batchQuery.Where(x => x.AskedAt > at || (x.AskedAt == at && x.QuestionId.CompareTo(id) > 0));
            var batch = await batchQuery.OrderBy(x => x.AskedAt).ThenBy(x => x.QuestionId).Take(100).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            IReadOnlyCollection<Question> matched = batch;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var matchedIds = await textSearch.MatchingIdsAsync(search, batch.Select(x => x.QuestionId).ToList(),
                    cancellationToken);
                matched = batch.Where(x => matchedIds.Contains(x.QuestionId)).ToList();
            }
            visible.AddRange(await VisibleAsync(matched, viewer, cancellationToken));
            scanAt = batch[^1].AskedAt; scanId = batch[^1].QuestionId;
            if (batch.Count < 100) break;
        }
        var page = visible.Take(take).ToList();
        var idsOnPage = page.Select(x => x.Question.QuestionId).ToList();
        var replies = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && idsOnPage.Contains(x.QuestionId))
            .GroupBy(x => x.QuestionId).Select(group => new { QuestionId = group.Key, Count = group.Count(), Last = group.Max(x => x.At) })
            .ToDictionaryAsync(x => x.QuestionId, cancellationToken);
        var names = await NamesAsync(page.Select(x => x.Question).ToList(), cancellationToken);
        string Name(Guid id) => names.GetValueOrDefault(id.ToString("D"), "Former staff member");
        var items = page.Select(pair =>
        {
            var question = pair.Question; var activity = replies.GetValueOrDefault(question.QuestionId);
            return new MyQuestionItem(question.QuestionId, question.Title, question.Status, question.RecordKind,
                question.RecordId, pair.Record.DisplayName!, Name(question.AskedBy),
                question.DirectedToUser is { } directed ? Name(directed) : RoleLabel(question.DirectedToRole!),
                question.AskedAt, activity?.Last ?? question.AskedAt, activity?.Count ?? 0,
                Math.Max(0, (clock.UtcNow.Date - question.AskedAt.Date).Days));
        }).ToList();
        var summary = await SummaryAsync(viewer, cancellationToken);
        return new MyQuestionPage(items, visible.Count > take && page.Count > 0
            ? Encode(page[^1].Question.AskedAt, page[^1].Question.QuestionId) : null, summary);
    }

    public async Task<QuestionSearchPage> SearchAsync(string query, bool answeredOnly, string? cursor, int take,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 50) throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 50.");
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return new QuestionSearchPage([], [], null);
        QuestionTextCursor? before = !string.IsNullOrWhiteSpace(cursor) ? DecodeSearch(cursor) : null;
        var ordered = new List<(Question Question, QuestionTextMatch Match, RecordReference Record)>();
        while (ordered.Count < take + 1)
        {
            const int scanSize = 100;
            var matches = await textSearch.SearchAsync(query, answeredOnly, scanSize, before, cancellationToken);
            if (matches.Count == 0) break;
            var matchIds = matches.Select(match => match.QuestionId).ToList();
            var questions = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && matchIds.Contains(x.QuestionId)).ToListAsync(cancellationToken);
            var byId = questions.ToDictionary(x => x.QuestionId);
            var candidates = matches.Where(match => byId.ContainsKey(match.QuestionId))
                .Select(match => (Question: byId[match.QuestionId], Match: match)).ToList();
            var visible = await VisibleAsync(candidates.Select(x => x.Question).ToList(), viewer, cancellationToken);
            var visibleById = visible.ToDictionary(x => x.Question.QuestionId, x => x.Record);
            ordered.AddRange(candidates.Where(x => visibleById.ContainsKey(x.Question.QuestionId))
                .Select(x => (x.Question, x.Match, visibleById[x.Question.QuestionId])));
            var last = matches[^1];
            before = new QuestionTextCursor(last.Rank, last.QuestionId);
            if (matches.Count < scanSize) break;
        }
        ordered = ordered.Take(take + 1).ToList();
        var page = ordered.Take(take).ToList();
        var acceptedIds = page.Select(x => x.Question.AcceptedReplyId).OfType<Guid>().ToList();
        var answers = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && acceptedIds.Contains(x.ReplyId))
            .ToDictionaryAsync(x => x.ReplyId, x => x.Body, cancellationToken);
        var items = page.Select(x => new QuestionSearchItem(x.Question.QuestionId, x.Question.Title, x.Question.Status,
            x.Question.RecordKind, x.Question.RecordId, x.Record.DisplayName!,
            x.Question.AcceptedReplyId is { } replyId ? answers.GetValueOrDefault(replyId, x.Question.Body) : x.Question.Body,
            x.Match.Rank, x.Question.AskedAt)).ToList();
        return new QuestionSearchPage([], items, ordered.Count > take && page.Count > 0
            ? EncodeSearch(page[^1].Match.Rank, page[^1].Question.QuestionId) : null);
    }

    public async Task<IReadOnlyList<QuestionRevisionView>?> RevisionsAsync(Guid questionId, Guid? replyId,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId, cancellationToken);
        if (question is null) return null;
        EnsureVisible(await records.OneAsync(question.RecordKind, question.RecordId, viewer, cancellationToken));
        if (replyId is { } expected && !await db.Replies.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId && x.ReplyId == expected, cancellationToken)) return null;
        var rows = await db.Revisions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.QuestionId == questionId && x.ReplyId == replyId && x.QuestionBody == (replyId == null))
            .OrderByDescending(x => x.RevisedAt).ThenByDescending(x => x.RevisionId).ToListAsync(cancellationToken);
        var names = await users.DisplayNamesAsync(rows.Select(x => x.RevisedBy.ToString("D")).Distinct().ToList(), cancellationToken);
        return rows.Select(x => new QuestionRevisionView(x.RevisionId, x.Body, x.RevisedBy,
            names.GetValueOrDefault(x.RevisedBy.ToString("D"), "Former staff member"), x.RevisedAt)).ToList();
    }

    private IQueryable<Question> RelevantQuestions(RecordReferenceViewer viewer) => db.Questions.AsNoTracking()
        .Where(x => x.TenantId == tenant.TenantId && (x.AskedBy == viewer.UserId || x.DirectedToUser == viewer.UserId
            || (x.DirectedToRole != null && viewer.Roles.Contains(x.DirectedToRole))));

    private async Task<MyQuestionSummary> SummaryAsync(RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var open = await RelevantQuestions(viewer).Where(x => x.Status == Knowledge.Contracts.QuestionStatus.Open)
            .OrderBy(x => x.AskedAt).ToListAsync(cancellationToken);
        var visible = await VisibleAsync(open, viewer, cancellationToken);
        var age = visible.GroupBy(x => AgeKey((clock.UtcNow.Date - x.Question.AskedAt.Date).Days))
            .Select(group => new RankedQuestionCount(group.Key, AgeLabel(group.Key), group.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Label).ToList();
        var kind = visible.GroupBy(x => x.Question.RecordKind)
            .Select(group => new RankedQuestionCount(group.Key, RecordKindLabel(group.Key), group.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Label).ToList();
        return new MyQuestionSummary(age, kind);
    }

    private async Task<List<(Question Question, RecordReference Record)>> VisibleAsync(IReadOnlyCollection<Question> questions,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var found = new Dictionary<(string Kind, Guid Id), RecordReference>();
        foreach (var group in questions.GroupBy(x => x.RecordKind))
        foreach (var ids in group.Select(x => x.RecordId).Distinct().Chunk(IRecordReferenceResolver.MaxRecordIds))
        {
            var page = await records.ManyAsync(group.Key, ids, viewer, cancellationToken);
            foreach (var item in page) found[(group.Key, item.Key)] = item.Value;
        }
        return questions.Where(question => found.TryGetValue((question.RecordKind, question.RecordId), out var record)
                && record.Exists && record.MayView && !string.IsNullOrWhiteSpace(record.DisplayName))
            .Select(question => (question, found[(question.RecordKind, question.RecordId)])).ToList();
    }

    private async Task<Dictionary<Guid, int>> ReplyCountsAsync(IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) => await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.QuestionId)).GroupBy(x => x.QuestionId).Select(group => new { QuestionId = group.Key, Count = group.Count() })
        .ToDictionaryAsync(x => x.QuestionId, x => x.Count, cancellationToken);

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(IReadOnlyCollection<Question> questions,
        CancellationToken cancellationToken) => await users.DisplayNamesAsync(questions.Select(x => x.AskedBy)
        .Concat(questions.Where(x => x.DirectedToUser != null).Select(x => x.DirectedToUser!.Value)).Distinct()
        .Select(x => x.ToString("D")).ToList(), cancellationToken);

    private static void ValidateStatusAndTake(string? status, int take)
    {
        if (status is not null && !Knowledge.Contracts.QuestionStatus.All.Contains(status, StringComparer.Ordinal))
            throw new QuestionRuleException("question_status_unknown", $"'{status}' is not a question status.");
        if (take is < 1 or > 100) throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
    }

    private static (int? From, int? To) AgeRange(string? age) => age switch
    {
        null or "" => (null, null),
        "under_1_day" => (null, 1),
        "one_to_three_days" => (1, 4),
        "four_to_seven_days" => (4, 8),
        "over_seven_days" => (8, null),
        _ => throw new QuestionRuleException("question_age_unknown", $"'{age}' is not a question age filter."),
    };

    private static string AgeKey(int days) => days < 1 ? "under_1_day" : days <= 3 ? "one_to_three_days"
        : days <= 7 ? "four_to_seven_days" : "over_seven_days";
    private static string AgeLabel(string key) => key switch
    {
        "under_1_day" => "Under 1 day", "one_to_three_days" => "1–3 days",
        "four_to_seven_days" => "4–7 days", _ => "Over 7 days",
    };
    private static string RecordKindLabel(string kind) => RoleLabel(kind);

    private static void EnsureVisible(RecordReference record)
    {
        if (!record.Exists) throw new QuestionRuleException("record_not_found", "The record does not exist.");
        if (!record.MayView) throw new QuestionRuleException("record_not_visible", "The current user may not view this record.");
        if (string.IsNullOrWhiteSpace(record.DisplayName))
            throw new InvalidOperationException("A visible record reference must have a readable display name.");
    }

    private static string RoleLabel(string role) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(role.Replace('_', ' '));
    private static string Encode(DateTime at, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.Ticks}:{id:D}"));
    private static string EncodeSearch(int rank, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rank}:{id:D}"));

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

    private static QuestionTextCursor DecodeSearch(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return new QuestionTextCursor(int.Parse(parts[0], CultureInfo.InvariantCulture), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new QuestionRuleException("cursor_invalid", "The search cursor is invalid.");
        }
    }
}
