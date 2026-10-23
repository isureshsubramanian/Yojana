using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Demo;

/// <summary>Uses production full-text search when present and the database-test fallback on SQL Express.</summary>
public sealed class DemoQuestionTextSearch(KnowledgeDbContext db, ITenantContext tenant) : IQuestionTextSearch
{
    private readonly SqlServerFullTextQuestionSearch fullText = new(db, tenant);

    public async Task<IReadOnlyList<QuestionTextMatch>> SearchAsync(string query, bool answeredOnly, int take,
        QuestionTextCursor? before, CancellationToken cancellationToken)
    {
        try
        {
            return await fullText.SearchAsync(query, answeredOnly, take, before, cancellationToken);
        }
        catch (QuestionSearchUnavailableException)
        {
            return await LikeQuestionsAsync(query, answeredOnly, take, before, cancellationToken);
        }
    }

    public async Task<IReadOnlySet<Guid>> MatchingIdsAsync(string query,
        IReadOnlyCollection<Guid> questionIds, CancellationToken cancellationToken)
    {
        try
        {
            return await fullText.MatchingIdsAsync(query, questionIds, cancellationToken);
        }
        catch (QuestionSearchUnavailableException)
        {
            if (questionIds.Count == 0) return new HashSet<Guid>();
            var term = query.Trim();
            var matchingReplies = db.Replies.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
                    && questionIds.Contains(value.QuestionId) && value.Body.Contains(term))
                .Select(value => value.QuestionId);
            var ids = await db.Questions.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
                    && questionIds.Contains(value.QuestionId)
                    && (value.Title.Contains(term) || value.Body.Contains(term)
                        || matchingReplies.Contains(value.QuestionId)))
                .Select(value => value.QuestionId).ToListAsync(cancellationToken);
            return ids.ToHashSet();
        }
    }

    public async Task<IReadOnlyList<EntryTextMatch>> SearchEntriesAsync(string query, int take,
        EntryTextCursor? before, CancellationToken cancellationToken)
    {
        try
        {
            return await fullText.SearchEntriesAsync(query, take, before, cancellationToken);
        }
        catch (QuestionSearchUnavailableException)
        {
            var term = query.Trim();
            var rows = await db.Entries.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
                    && value.Status == KnowledgeEntryStatus.Published
                    && (value.Title.Contains(term) || value.Problem.Contains(term)
                        || value.Answer.Contains(term) || value.Steps.Contains(term)))
                .Select(value => new { value.EntryId, Title = value.Title.Contains(term) })
                .ToListAsync(cancellationToken);
            var ordered = rows.Select(value => new EntryTextMatch(value.EntryId, value.Title ? 2000 : 1000))
                .OrderByDescending(value => value.Rank).ThenByDescending(value => value.EntryId);
            if (before is not null)
                ordered = ordered.Where(value => value.Rank < before.Rank
                        || (value.Rank == before.Rank && value.EntryId.CompareTo(before.EntryId) < 0))
                    .OrderByDescending(value => value.Rank).ThenByDescending(value => value.EntryId);
            return ordered.Take(take).ToList();
        }
    }

    private async Task<IReadOnlyList<QuestionTextMatch>> LikeQuestionsAsync(string query,
        bool answeredOnly, int take, QuestionTextCursor? before, CancellationToken cancellationToken)
    {
        var term = query.Trim();
        var questions = db.Questions.AsNoTracking().Where(value => value.TenantId == tenant.TenantId);
        if (answeredOnly) questions = questions.Where(value => value.Status == QuestionStatus.Answered);
        var matchingReplies = db.Replies.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
                && value.Body.Contains(term)).Select(value => value.QuestionId);
        var rows = await questions.Where(value => value.Title.Contains(term) || value.Body.Contains(term)
                || matchingReplies.Contains(value.QuestionId))
            .Select(value => new
            {
                value.QuestionId,
                value.AcceptedReplyId,
                Title = value.Title.Contains(term),
            }).ToListAsync(cancellationToken);
        var ordered = rows.Select(value => new QuestionTextMatch(value.QuestionId,
                (value.AcceptedReplyId is null ? 0 : 1_000_000) + (value.Title ? 1000 : 500)))
            .OrderByDescending(value => value.Rank).ThenByDescending(value => value.QuestionId);
        if (before is not null)
            ordered = ordered.Where(value => value.Rank < before.Rank
                    || (value.Rank == before.Rank && value.QuestionId.CompareTo(before.QuestionId) < 0))
                .OrderByDescending(value => value.Rank).ThenByDescending(value => value.QuestionId);
        return ordered.Take(take).ToList();
    }
}
