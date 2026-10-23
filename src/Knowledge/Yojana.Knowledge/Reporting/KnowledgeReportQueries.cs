using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Knowledge.Reports;

public static class KnowledgeReportPeriod
{
    public const string ThirtyDays = "30_days";
    public const string NinetyDays = "90_days";
    public const string TwelveMonths = "12_months";
    public static readonly IReadOnlyList<string> All = [ThirtyDays, NinetyDays, TwelveMonths];
}

public sealed record KnowledgeReportFilters(string Period, string? DirectedRole = null,
    string? RecordKind = null, string? Topic = null);
public sealed record KnowledgeReportWindow(DateOnly From, DateOnly Through, string TimeZone,
    DateTime FromUtc, DateTime ToUtc);
public sealed record KnowledgeReportTiming(long? MedianSeconds, long? NinetiethPercentileSeconds, int Samples);
public sealed record KnowledgeReportRank(string Key, string Label, int Count);
public sealed record KnowledgeReportRecurrence(Guid EntryId, string Title, string ReviewedBy,
    int AskedAgain, int PointedToIt, DateTime LastAskedAt);
public sealed record KnowledgeReportAge(string Key, string Label, int Count);
public sealed record KnowledgeReportEntryRead(Guid EntryId, string Title, int Reads);
public sealed record KnowledgeReportTraining(int NewMembers, int Reads,
    IReadOnlyList<KnowledgeReportEntryRead> Entries);
public sealed record KnowledgeReportView(KnowledgeReportWindow Window, int Questions,
    KnowledgeReportTiming FirstReply, KnowledgeReportTiming AcceptedAnswer, int Unanswered,
    int PointedToAnEntry, IReadOnlyList<KnowledgeReportRank> ByDirectedRole,
    IReadOnlyList<KnowledgeReportRank> ByRecordKind, IReadOnlyList<KnowledgeReportRank> ByTopic,
    IReadOnlyList<KnowledgeReportRecurrence> AskedAgainDespiteEntry,
    IReadOnlyList<KnowledgeReportAge> UnansweredByAge, KnowledgeReportTraining ReadByNewTeamMembers,
    int SimilarEntryMinimumRank, string VisibilityNote, string SimilarityNote);

internal sealed class TimeMetricRow
{
    public long? FirstReplyMedian { get; set; }
    public long? FirstReplyP90 { get; set; }
    public int FirstReplySamples { get; set; }
    public long? AcceptedMedian { get; set; }
    public long? AcceptedP90 { get; set; }
    public int AcceptedSamples { get; set; }
}

public sealed class KnowledgeReportQueries(KnowledgeDbContext db, KnowledgeReportSettings settings,
    RecordReferenceLookup records, ITenantTrainingDirectory trainingDirectory, IUserDirectory users,
    ITenantTimeZoneReadPort timeZones, ITenantContext tenant, IClock clock)
{
    public const string NoEntryTopic = "no_entry_yet";
    public const string UntaggedEntryTopic = "untagged_entry";
    public const int ExportPageSize = 500;

    public async Task<KnowledgeReportView> ReportAsync(KnowledgeReportFilters filters,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        await EnsureAllowedAsync(viewer, cancellationToken);
        var window = await WindowAsync(filters.Period, cancellationToken);
        var visibleIds = await VisibleQuestionIdsAsync(window, viewer, cancellationToken);
        var filteredIds = await FilteredQuestionIdsAsync(visibleIds, window, filters, cancellationToken);
        var threshold = await settings.SimilarEntryRankAsync(cancellationToken);

        var metrics = await TimeMetricsAsync(filteredIds, cancellationToken);
        var questions = db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && filteredIds.Contains(x.QuestionId));
        var total = filteredIds.Count;
        var unanswered = await questions.CountAsync(x => x.Status == QuestionStatus.Open, cancellationToken);
        var byRole = await questions.Where(x => x.DirectedToRole != null)
            .GroupBy(x => x.DirectedToRole!).Select(x => new { Key = x.Key, Count = x.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(8).ToListAsync(cancellationToken);
        var byKind = await questions.GroupBy(x => x.RecordKind)
            .Select(x => new { Key = x.Key, Count = x.Count() }).OrderByDescending(x => x.Count)
            .ThenBy(x => x.Key).Take(8).ToListAsync(cancellationToken);
        var byTopic = await TopicsAsync(filteredIds, cancellationToken);
        var recurrence = await RecurrenceAsync(filteredIds, cancellationToken);
        var pointed = recurrence.Sum(x => x.PointedToIt);
        var ages = await AgesAsync(questions, cancellationToken);
        var training = await TrainingAsync(window, cancellationToken);

        return new KnowledgeReportView(window, total,
            new KnowledgeReportTiming(metrics.FirstReplyMedian, metrics.FirstReplyP90, metrics.FirstReplySamples),
            new KnowledgeReportTiming(metrics.AcceptedMedian, metrics.AcceptedP90, metrics.AcceptedSamples),
            unanswered, pointed,
            byRole.Select(x => new KnowledgeReportRank(x.Key, Label(x.Key), x.Count)).ToList(),
            byKind.Select(x => new KnowledgeReportRank(x.Key, RecordKindLabel(x.Key), x.Count)).ToList(),
            byTopic, recurrence, ages, training, threshold,
            "Totals and ranked lists include only questions whose record the current viewer may open; hidden record titles are never loaded.",
            $"Asked again means the existing similar-question search offered a published entry when the question was asked with SQL Server full-text rank {threshold} or higher. The threshold is a tenant setting.");
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportAsync(KnowledgeReportFilters filters,
        RecordReferenceViewer viewer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureAllowedAsync(viewer, cancellationToken);
        var window = await WindowAsync(filters.Period, cancellationToken);
        DateTime? afterAt = null;
        Guid? afterId = null;
        while (true)
        {
            var query = db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.AskedAt >= window.FromUtc && x.AskedAt < window.ToUtc);
            query = ApplyFilters(query, filters);
            if (afterAt is { } at && afterId is { } id)
                query = query.Where(x => x.AskedAt > at || (x.AskedAt == at && x.QuestionId.CompareTo(id) > 0));
            var page = await query.OrderBy(x => x.AskedAt).ThenBy(x => x.QuestionId).Take(ExportPageSize)
                .Select(x => new { x.QuestionId, x.RecordKind, x.RecordId, x.Title, x.Body, x.Status,
                    x.DirectedToRole, x.AskedAt, x.AcceptedAt }).ToListAsync(cancellationToken);
            if (page.Count == 0) yield break;
            var visibility = await ResolveVisibilityAsync(page.Select(x => (x.QuestionId, x.RecordKind, x.RecordId)),
                viewer, cancellationToken);
            var ids = page.Select(x => x.QuestionId).ToList();
            var replies = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && ids.Contains(x.QuestionId)).GroupBy(x => x.QuestionId)
                .Select(x => new { QuestionId = x.Key, First = x.Min(y => y.At) })
                .ToDictionaryAsync(x => x.QuestionId, x => x.First, cancellationToken);
            var tags = await (from entry in db.Entries.AsNoTracking()
                join tag in db.EntryTags.AsNoTracking()
                    on new { entry.TenantId, entry.EntryId, Version = entry.CurrentVersion }
                    equals new { tag.TenantId, tag.EntryId, tag.Version }
                where entry.TenantId == tenant.TenantId && ids.Contains(entry.SourceQuestionId)
                select new { entry.SourceQuestionId, tag.Label }).ToListAsync(cancellationToken);
            var entryQuestions = await db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && ids.Contains(x.SourceQuestionId)).Select(x => x.SourceQuestionId)
                .ToHashSetAsync(cancellationToken);
            var tagsByQuestion = tags.GroupBy(x => x.SourceQuestionId)
                .ToDictionary(x => x.Key, x => string.Join("; ", x.Select(y => y.Label).Distinct().Order()));
            foreach (var row in page)
            {
                var mayRead = visibility.GetValueOrDefault(row.QuestionId);
                replies.TryGetValue(row.QuestionId, out var firstReply);
                yield return
                [
                    Local(row.AskedAt, window.TimeZone), row.Status, RecordKindLabel(row.RecordKind),
                    row.DirectedToRole is null ? "Directed to a person" : Label(row.DirectedToRole),
                    mayRead ? row.Title : null, mayRead ? row.Body : null,
                    firstReply == default ? null : Local(firstReply, window.TimeZone),
                    row.AcceptedAt is null ? null : Local(row.AcceptedAt.Value, window.TimeZone),
                    firstReply == default ? null : Seconds(row.AskedAt, firstReply),
                    row.AcceptedAt is null ? null : Seconds(row.AskedAt, row.AcceptedAt.Value),
                    tagsByQuestion.GetValueOrDefault(row.QuestionId,
                        entryQuestions.Contains(row.QuestionId) ? "Untagged entry" : "No entry yet"),
                ];
            }
            afterAt = page[^1].AskedAt;
            afterId = page[^1].QuestionId;
        }
    }

    public async Task EnsureAllowedAsync(RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var allowed = await settings.AllowedRolesAsync(cancellationToken);
        if (!viewer.Roles.Any(role => allowed.Contains(role, StringComparer.Ordinal)))
            throw new QuestionRuleException("knowledge_report_role_required",
                "A role selected in the tenant's knowledge-report setting is required.");
    }

    private async Task<KnowledgeReportWindow> WindowAsync(string period, CancellationToken cancellationToken)
    {
        if (!KnowledgeReportPeriod.All.Contains(period, StringComparer.Ordinal))
            throw new QuestionRuleException("knowledge_report_period_unknown", "Choose 30 days, 90 days or 12 months.");
        var zoneId = await timeZones.GetAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc), zone);
        var through = DateOnly.FromDateTime(localNow);
        var from = period switch
        {
            KnowledgeReportPeriod.ThirtyDays => through.AddDays(-29),
            KnowledgeReportPeriod.NinetyDays => through.AddDays(-89),
            _ => through.AddMonths(-12),
        };
        return new KnowledgeReportWindow(from, through, zoneId, ToUtc(from, zone), ToUtc(through.AddDays(1), zone));
    }

    private async Task<List<Guid>> VisibleQuestionIdsAsync(KnowledgeReportWindow window, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var rows = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.AskedAt >= window.FromUtc && x.AskedAt < window.ToUtc)
            .Select(x => new { x.QuestionId, x.RecordKind, x.RecordId }).ToListAsync(cancellationToken);
        var visibility = await ResolveVisibilityAsync(rows.Select(x => (x.QuestionId, x.RecordKind, x.RecordId)),
            viewer, cancellationToken);
        return rows.Where(x => visibility.GetValueOrDefault(x.QuestionId)).Select(x => x.QuestionId).ToList();
    }

    private async Task<Dictionary<Guid, bool>> ResolveVisibilityAsync(
        IEnumerable<(Guid QuestionId, string RecordKind, Guid RecordId)> rows, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, bool>();
        foreach (var kind in rows.GroupBy(x => x.RecordKind))
        {
            var references = new Dictionary<Guid, RecordReference>();
            foreach (var chunk in kind.Select(x => x.RecordId).Distinct().Chunk(IRecordReferenceResolver.MaxRecordIds))
                foreach (var pair in await records.ManyAsync(kind.Key, chunk, viewer, cancellationToken))
                    references[pair.Key] = pair.Value;
            foreach (var row in kind)
                result[row.QuestionId] = references.TryGetValue(row.RecordId, out var found) && found.MayView;
        }
        return result;
    }

    private async Task<List<Guid>> FilteredQuestionIdsAsync(List<Guid> visibleIds, KnowledgeReportWindow window,
        KnowledgeReportFilters filters, CancellationToken cancellationToken)
    {
        var query = db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && visibleIds.Contains(x.QuestionId) && x.AskedAt >= window.FromUtc && x.AskedAt < window.ToUtc);
        return await ApplyFilters(query, filters).OrderBy(x => x.QuestionId).Select(x => x.QuestionId)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Question> ApplyFilters(IQueryable<Question> query, KnowledgeReportFilters filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.DirectedRole))
            query = query.Where(x => x.DirectedToRole == filters.DirectedRole);
        if (!string.IsNullOrWhiteSpace(filters.RecordKind))
            query = query.Where(x => x.RecordKind == filters.RecordKind);
        if (filters.Topic == NoEntryTopic)
            query = query.Where(question => !db.Entries.Any(entry => entry.TenantId == tenant.TenantId
                && entry.SourceQuestionId == question.QuestionId));
        else if (filters.Topic == UntaggedEntryTopic)
            query = query.Where(question => db.Entries.Any(entry => entry.TenantId == tenant.TenantId
                && entry.SourceQuestionId == question.QuestionId && !db.EntryTags.Any(tag =>
                    tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId
                    && tag.Version == entry.CurrentVersion && tag.Kind != KnowledgeTagKind.Role)));
        else if (!string.IsNullOrWhiteSpace(filters.Topic))
        {
            var parts = filters.Topic.Split('|', 2);
            if (parts.Length != 2) throw new QuestionRuleException("knowledge_report_topic_unknown",
                "The topic filter is unknown.");
            var kind = parts[0];
            var value = parts[1];
            query = query.Where(question => db.Entries.Any(entry => entry.TenantId == tenant.TenantId
                && entry.SourceQuestionId == question.QuestionId && db.EntryTags.Any(tag =>
                    tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId
                    && tag.Version == entry.CurrentVersion && tag.Kind == kind && tag.Value == value)));
        }
        return query;
    }

    private async Task<TimeMetricRow> TimeMetricsAsync(List<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new TimeMetricRow();
        const string sql = """
            WITH ids AS (SELECT CONVERT(uniqueidentifier, [value]) question_id FROM OPENJSON(@ids)),
            first_reply AS (
                SELECT q.question_id, DATEDIFF_BIG(second, q.asked_at, MIN(r.at)) seconds
                FROM knowledge.question q JOIN ids ON ids.question_id=q.question_id
                JOIN knowledge.question_reply r ON r.tenant_id=q.tenant_id AND r.question_id=q.question_id
                WHERE q.tenant_id=@tenant GROUP BY q.question_id, q.asked_at
            ), accepted AS (
                SELECT DATEDIFF_BIG(second, q.asked_at, q.accepted_at) seconds
                FROM knowledge.question q JOIN ids ON ids.question_id=q.question_id
                WHERE q.tenant_id=@tenant AND q.accepted_at IS NOT NULL
            )
            SELECT
              (SELECT TOP (1) CONVERT(bigint, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY seconds) OVER ()) FROM first_reply) FirstReplyMedian,
              (SELECT TOP (1) CONVERT(bigint, PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY seconds) OVER ()) FROM first_reply) FirstReplyP90,
              (SELECT COUNT(*) FROM first_reply) FirstReplySamples,
              (SELECT TOP (1) CONVERT(bigint, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY seconds) OVER ()) FROM accepted) AcceptedMedian,
              (SELECT TOP (1) CONVERT(bigint, PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY seconds) OVER ()) FROM accepted) AcceptedP90,
              (SELECT COUNT(*) FROM accepted) AcceptedSamples
            """;
        var rows = await db.Database.SqlQueryRaw<TimeMetricRow>(sql,
            new SqlParameter("@ids", JsonSerializer.Serialize(ids)),
            new SqlParameter("@tenant", tenant.TenantId.Value)).ToListAsync(cancellationToken);
        return rows.Single();
    }

    private async Task<IReadOnlyList<KnowledgeReportRank>> TopicsAsync(IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var tagged = await (from question in db.Questions.AsNoTracking()
            join entry in db.Entries.AsNoTracking()
                on new { question.TenantId, QuestionId = question.QuestionId }
                equals new { entry.TenantId, QuestionId = entry.SourceQuestionId }
            join tag in db.EntryTags.AsNoTracking()
                on new { entry.TenantId, entry.EntryId, Version = entry.CurrentVersion }
                equals new { tag.TenantId, tag.EntryId, tag.Version }
            where question.TenantId == tenant.TenantId && ids.Contains(question.QuestionId)
                && tag.Kind != KnowledgeTagKind.Role
            group tag by new { tag.Kind, tag.Value, tag.Label } into grouped
            select new { grouped.Key.Kind, grouped.Key.Value, grouped.Key.Label, Count = grouped.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Label).Take(8).ToListAsync(cancellationToken);
        var noEntry = await db.Questions.AsNoTracking().CountAsync(question => question.TenantId == tenant.TenantId
            && ids.Contains(question.QuestionId) && !db.Entries.Any(entry => entry.TenantId == tenant.TenantId
                && entry.SourceQuestionId == question.QuestionId), cancellationToken);
        var untagged = await db.Questions.AsNoTracking().CountAsync(question => question.TenantId == tenant.TenantId
            && ids.Contains(question.QuestionId) && db.Entries.Any(entry => entry.TenantId == tenant.TenantId
                && entry.SourceQuestionId == question.QuestionId && !db.EntryTags.Any(tag =>
                    tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId
                    && tag.Version == entry.CurrentVersion && tag.Kind != KnowledgeTagKind.Role)), cancellationToken);
        var result = tagged.Select(x => new KnowledgeReportRank($"{x.Kind}|{x.Value}", x.Label, x.Count)).ToList();
        if (noEntry > 0) result.Add(new KnowledgeReportRank(NoEntryTopic, "No entry yet", noEntry));
        if (untagged > 0) result.Add(new KnowledgeReportRank(UntaggedEntryTopic, "Entry without a topic", untagged));
        return result.OrderByDescending(x => x.Count).ThenBy(x => x.Label).Take(8).ToList();
    }

    private async Task<IReadOnlyList<KnowledgeReportRecurrence>> RecurrenceAsync(IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var rows = await (from match in db.SimilarEntries.AsNoTracking()
            join question in db.Questions.AsNoTracking()
                on new { match.TenantId, match.QuestionId } equals new { question.TenantId, question.QuestionId }
            join entry in db.Entries.AsNoTracking()
                on new { match.TenantId, match.EntryId } equals new { entry.TenantId, entry.EntryId }
            where match.TenantId == tenant.TenantId && ids.Contains(match.QuestionId)
            select new { match.QuestionId, match.EntryId, entry.Title, entry.PublishedBy, question.AskedAt })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];
        var questionIds = rows.Select(x => x.QuestionId).Distinct().ToList();
        var replies = await db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && questionIds.Contains(x.QuestionId)).Select(x => new { x.QuestionId, x.Body }).ToListAsync(cancellationToken);
        var names = await users.DisplayNamesAsync(rows.Where(x => x.PublishedBy != null)
            .Select(x => x.PublishedBy!.Value.ToString("D")).Distinct().ToList(), cancellationToken);
        return rows.GroupBy(x => new { x.EntryId, x.Title, x.PublishedBy }).Select(group =>
            new KnowledgeReportRecurrence(group.Key.EntryId, group.Key.Title,
                group.Key.PublishedBy is { } reviewer
                    ? names.GetValueOrDefault(reviewer.ToString("D"), "Former staff member") : "Reviewer unavailable",
                group.Select(x => x.QuestionId).Distinct().Count(),
                group.Count(x => replies.Any(reply => reply.QuestionId == x.QuestionId
                    && reply.Body.Contains($"/knowledge/{x.EntryId:D}", StringComparison.OrdinalIgnoreCase))),
                group.Max(x => x.AskedAt))).OrderByDescending(x => x.AskedAgain).ThenBy(x => x.Title).Take(10)
            .ToList();
    }

    private async Task<IReadOnlyList<KnowledgeReportAge>> AgesAsync(IQueryable<Question> questions,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var rows = await questions.Where(x => x.Status == QuestionStatus.Open).GroupBy(_ => 1).Select(group => new
        {
            UnderFourHours = group.Count(x => x.AskedAt > now.AddHours(-4)),
            FourToTwentyFourHours = group.Count(x => x.AskedAt <= now.AddHours(-4) && x.AskedAt > now.AddDays(-1)),
            OneToThreeDays = group.Count(x => x.AskedAt <= now.AddDays(-1) && x.AskedAt > now.AddDays(-3)),
            OverThreeDays = group.Count(x => x.AskedAt <= now.AddDays(-3)),
        }).SingleOrDefaultAsync(cancellationToken);
        return
        [
            new("under_4_hours", "Under 4 hours", rows?.UnderFourHours ?? 0),
            new("4_to_24_hours", "4 to 24 hours", rows?.FourToTwentyFourHours ?? 0),
            new("1_to_3_days", "1 to 3 days", rows?.OneToThreeDays ?? 0),
            new("over_3_days", "Over 3 days", rows?.OverThreeDays ?? 0),
        ];
    }

    private async Task<KnowledgeReportTraining> TrainingAsync(KnowledgeReportWindow window,
        CancellationToken cancellationToken)
    {
        var members = await trainingDirectory.AllActiveMembersAsync(cancellationToken);
        var joined = members.ToDictionary(x => x.UserId, x => x.MembershipCreatedAt);
        var ids = joined.Keys.ToList();
        var reads = ids.Count == 0 ? [] : await db.TrainingProgress.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && ids.Contains(x.UserId)
                && x.ReadAt >= window.FromUtc && x.ReadAt < window.ToUtc)
            .Select(x => new { x.UserId, x.EntryId, x.ReadAt }).ToListAsync(cancellationToken);
        var newReads = reads.Where(x => x.ReadAt >= joined[x.UserId] && x.ReadAt < joined[x.UserId].AddDays(30))
            .ToList();
        var entryIds = newReads.Select(x => x.EntryId).Distinct().ToList();
        var titles = entryIds.Count == 0 ? [] : await db.Entries.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && entryIds.Contains(x.EntryId))
            .Select(x => new { x.EntryId, x.Title }).ToListAsync(cancellationToken);
        var items = titles.Select(entry => new KnowledgeReportEntryRead(entry.EntryId, entry.Title,
                newReads.Count(x => x.EntryId == entry.EntryId)))
            .OrderByDescending(x => x.Reads).ThenBy(x => x.Title).Take(10).ToList();
        return new KnowledgeReportTraining(newReads.Select(x => x.UserId).Distinct().Count(), newReads.Count, items);
    }

    private static DateTime ToUtc(DateOnly date, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);
    private static string Local(DateTime utc, string zone) => TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(zone))
        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Seconds(DateTime from, DateTime to) => Convert.ToInt64((to - from).TotalSeconds)
        .ToString(CultureInfo.InvariantCulture);
    // Sentence case, as role names read everywhere else in the product ("Service desk").
    private static string Label(string value)
    {
        var words = value.Replace('_', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
    private static string RecordKindLabel(string value) => value switch
    {
        RecordKind.Rma => "Return (RMA)", RecordKind.RefurbishmentWorkOrder => "Refurbishment work order",
        RecordKind.WarrantyPolicy => "Warranty policy", RecordKind.ImportJob => "Import job",
        RecordKind.IntegrationMessage => "Integration message", _ => Label(value),
    };
}
