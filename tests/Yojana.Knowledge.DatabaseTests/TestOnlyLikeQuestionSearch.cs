using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.DatabaseTests;

/// <summary>
/// Test-only substitute for the Codex SQL Express image, which has no full-text feature. Production registration never
/// selects this implementation; it exists so visibility, paging and accepted-answer ranking remain database-tested.
/// </summary>
public sealed class TestOnlyLikeQuestionSearch(KnowledgeDbContext db, ITenantContext tenant) : IQuestionTextSearch
{
    public async Task<IReadOnlyList<QuestionTextMatch>> SearchAsync(string query, bool answeredOnly, int take,
        QuestionTextCursor? before,
        CancellationToken cancellationToken)
    {
        var term = query.Trim();
        var questions = db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (answeredOnly) questions = questions.Where(x => x.Status == Knowledge.Contracts.QuestionStatus.Answered);
        var matchingReplies = db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.Body.Contains(term)).Select(x => x.QuestionId);
        var rows = await questions.Where(x => x.Title.Contains(term) || x.Body.Contains(term)
                || matchingReplies.Contains(x.QuestionId))
            .Select(x => new { x.QuestionId, x.AcceptedReplyId, Title = x.Title.Contains(term) })
            .ToListAsync(cancellationToken);
        var ordered = rows.Select(x => new QuestionTextMatch(x.QuestionId,
                (x.AcceptedReplyId is null ? 0 : 1_000_000) + (x.Title ? 1000 : 500)))
            .OrderByDescending(x => x.Rank).ThenByDescending(x => x.QuestionId);
        if (before is not null)
            ordered = ordered.Where(x => x.Rank < before.Rank ||
                (x.Rank == before.Rank && x.QuestionId.CompareTo(before.QuestionId) < 0))
                .OrderByDescending(x => x.Rank).ThenByDescending(x => x.QuestionId);
        return ordered.Take(take).ToList();
    }

    public async Task<IReadOnlySet<Guid>> MatchingIdsAsync(string query, IReadOnlyCollection<Guid> questionIds,
        CancellationToken cancellationToken)
    {
        var term = query.Trim();
        var matchingReplies = db.Replies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && questionIds.Contains(x.QuestionId) && x.Body.Contains(term)).Select(x => x.QuestionId);
        var ids = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && questionIds.Contains(x.QuestionId) && (x.Title.Contains(term) || x.Body.Contains(term)
                    || matchingReplies.Contains(x.QuestionId)))
            .Select(x => x.QuestionId).ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }
}
