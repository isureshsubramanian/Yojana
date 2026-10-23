using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Data;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Settings;

public sealed class KnowledgeScreenChangesSettingsSummaryContributor(
    KnowledgeDbContext db,
    ITenantContext tenant) : ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "knowledge_screen_changes";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } =
        [new(Key, [YojanaRole.TenantAdministrator])];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];
        var rows = await db.Database.SqlQuery<CountRow>($"""
            SELECT grouped.metric AS [Metric], COUNT_BIG(DISTINCT grouped.value) AS [Value]
            FROM
            (
                SELECT N'screensMarked' AS metric, change.screen_reference AS value
                FROM [knowledge].[knowledge_screen_change] AS change
                WHERE change.tenant_id = {tenant.TenantId.Value}

                UNION ALL

                SELECT N'entriesToReview' AS metric, CONVERT(nvarchar(36), entry.entry_id) AS value
                FROM [knowledge].[knowledge_entry] AS entry
                WHERE entry.tenant_id = {tenant.TenantId.Value}
                  AND entry.needs_review = CAST(1 AS bit)
            ) AS grouped
            GROUP BY grouped.metric
            """).ToListAsync(cancellationToken);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["screensMarked"] = 0,
            ["entriesToReview"] = 0,
        };
        foreach (var row in rows) counts[row.Metric] = row.Value;
        return [new SettingsSummaryLine(Key, counts, null)];
    }

    private sealed class CountRow
    {
        public required string Metric { get; init; }
        public long Value { get; init; }
    }
}
