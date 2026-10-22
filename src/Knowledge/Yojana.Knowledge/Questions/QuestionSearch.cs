using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Yojana.Knowledge.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Questions;

public sealed record QuestionTextMatch(Guid QuestionId, int Rank);
public sealed record QuestionTextCursor(int Rank, Guid QuestionId);

/// <summary>
/// Narrow seam around SQL Server full-text. Test hosts whose SQL edition has no full-text feature replace only this
/// service; production never falls back to a substring scan.
/// </summary>
public interface IQuestionTextSearch
{
    Task<IReadOnlyList<QuestionTextMatch>> SearchAsync(string query, bool answeredOnly, int take,
        QuestionTextCursor? before,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> MatchingIdsAsync(string query, IReadOnlyCollection<Guid> questionIds,
        CancellationToken cancellationToken);
}

public sealed class SqlServerFullTextQuestionSearch(KnowledgeDbContext db, ITenantContext tenant)
    : IQuestionTextSearch
{
    public async Task<IReadOnlyList<QuestionTextMatch>> SearchAsync(string query, bool answeredOnly, int take,
        QuestionTextCursor? before,
        CancellationToken cancellationToken)
    {
        var fullText = FullTextExpression(query);
        if (fullText is null) return [];
        await EnsureAvailableAsync(cancellationToken);

        // Coupled to the unique index names created by P13T04Search: SQL Server full-text keys must be single-column,
        // while tenant isolation remains in the composite primary keys and RLS predicates. Renaming either index
        // without rebuilding the matching full-text index makes this query unavailable.
        const string sql = """
            WITH matches AS (
                SELECT q.question_id, ft.[RANK]
                FROM CONTAINSTABLE(knowledge.question, (title, body), @query) ft
                JOIN knowledge.question q ON q.question_id = ft.[KEY]
                WHERE q.tenant_id = @tenant AND (@answeredOnly = 0 OR q.status = 'answered')
                UNION ALL
                SELECT q.question_id, ft.[RANK]
                FROM CONTAINSTABLE(knowledge.question_reply, body, @query) ft
                JOIN knowledge.question_reply r ON r.reply_id = ft.[KEY]
                JOIN knowledge.question q ON q.tenant_id = r.tenant_id AND q.question_id = r.question_id
                WHERE q.tenant_id = @tenant AND (@answeredOnly = 0 OR q.status = 'answered')
            )
            , ranked AS (
                SELECT m.question_id AS QuestionId,
                    MAX(m.[RANK]) + CASE WHEN q.accepted_reply_id IS NULL THEN 0 ELSE 1000000 END AS [Rank]
                FROM matches m
                JOIN knowledge.question q ON q.tenant_id = @tenant AND q.question_id = m.question_id
                GROUP BY m.question_id, q.accepted_reply_id
            )
            SELECT TOP (@take) QuestionId, [Rank]
            FROM ranked
            WHERE @beforeRank IS NULL OR [Rank] < @beforeRank
                OR ([Rank] = @beforeRank AND QuestionId < @beforeId)
            ORDER BY [Rank] DESC, QuestionId DESC
            """;
        return await db.Database.SqlQueryRaw<QuestionTextMatch>(sql,
            new SqlParameter("@query", fullText), new SqlParameter("@tenant", tenant.TenantId.Value),
            new SqlParameter("@answeredOnly", answeredOnly), new SqlParameter("@take", take),
            new SqlParameter("@beforeRank", SqlDbType.Int) { Value = (object?)before?.Rank ?? DBNull.Value },
            new SqlParameter("@beforeId", SqlDbType.UniqueIdentifier) { Value = (object?)before?.QuestionId ?? DBNull.Value })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> MatchingIdsAsync(string query, IReadOnlyCollection<Guid> questionIds,
        CancellationToken cancellationToken)
    {
        if (questionIds.Count == 0) return new HashSet<Guid>();
        var fullText = FullTextExpression(query);
        if (fullText is null) return new HashSet<Guid>();
        await EnsureAvailableAsync(cancellationToken);
        const string sql = """
            WITH matches AS (
                SELECT q.question_id
                FROM CONTAINSTABLE(knowledge.question, (title, body), @query) ft
                JOIN knowledge.question q ON q.question_id = ft.[KEY]
                WHERE q.tenant_id = @tenant
                UNION
                SELECT r.question_id
                FROM CONTAINSTABLE(knowledge.question_reply, body, @query) ft
                JOIN knowledge.question_reply r ON r.reply_id = ft.[KEY]
                WHERE r.tenant_id = @tenant
            )
            SELECT DISTINCT ids.question_id AS QuestionId, 0 AS [Rank]
            FROM OPENJSON(@ids) WITH (question_id uniqueidentifier '$') ids
            JOIN matches m ON m.question_id = ids.question_id
            """;
        var json = System.Text.Json.JsonSerializer.Serialize(questionIds);
        var rows = await db.Database.SqlQueryRaw<QuestionTextMatch>(sql, new SqlParameter("@ids", json),
            new SqlParameter("@tenant", tenant.TenantId.Value), new SqlParameter("@query", fullText))
            .ToListAsync(cancellationToken);
        return rows.Select(x => x.QuestionId).ToHashSet();
    }

    private async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        var installed = await db.Database.SqlQueryRaw<int>(
            "SELECT CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')) AS [Value]")
            .SingleAsync(cancellationToken);
        if (installed != 1) throw new QuestionSearchUnavailableException();
    }

    private static string? FullTextExpression(string input)
    {
        var terms = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => new string(term.Where(char.IsLetterOrDigit).ToArray()))
            .Where(term => term.Length >= 2).Take(8).ToList();
        return terms.Count == 0 ? null : string.Join(" AND ", terms.Select(term => $"\"{term}*\""));
    }
}

public sealed class QuestionSearchUnavailableException : Exception
{
    public QuestionSearchUnavailableException()
        : base("Question search requires SQL Server full-text search, which is not installed on this server.") { }
}
