using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Queries;

/// <summary>
/// Narrow read boundary used by campaigns. Identifier, lot, as-built, attribute-history and episode facts stay behind
/// this query service; campaign handlers receive ids and recipient snapshots and never join another aggregate's writes.
/// </summary>
public sealed class CampaignSelectionFacts(SerialisationDbContext db, ITenantContext tenant)
{
    private const int QueryChunk = 2000;

    public async Task<IReadOnlySet<Guid>> MatchingUnitIdsAsync(IReadOnlyList<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        if (criteria.Count == 0) throw new CommandRefusedException("campaign_criteria_required", "A campaign needs at least one criterion before preview or selection.");
        HashSet<Guid>? result = null;
        foreach (var group in criteria.GroupBy(c => c.Kind, StringComparer.Ordinal))
        {
            var matches = group.Key switch
            {
                CampaignCriterionKind.IdentifierRange => await IdentifierRangesAsync(group.ToList(), cancellationToken),
                CampaignCriterionKind.Lot => await LotsAsync(group.ToList(), cancellationToken),
                CampaignCriterionKind.Child => await ChildrenAsync(group.ToList(), cancellationToken),
                CampaignCriterionKind.Firmware => await FirmwareAsync(group.ToList(), cancellationToken),
                _ => throw new CommandRefusedException("campaign_criterion_kind_unknown", $"Campaign criterion kind '{group.Key}' is not known."),
            };
            if (result is null) result = matches;
            else result.IntersectWith(matches);
            if (result.Count == 0) break;
        }

        return result ?? new HashSet<Guid>();
    }

    public async Task<IReadOnlyList<CampaignMatchRow>> CurrentMatchesAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        var facts = await CurrentRecipientFactsAsync(unitIds, cancellationToken);
        return facts.Select(f => new CampaignMatchRow(f.UnitId, f.Serial, f.OwnerPartyRef, f.OwnerPartyType, f.CustodianPartyRef, f.CustodianPartyType)).ToList();
    }

    public async Task<IReadOnlyList<CampaignUnit>> SnapshotRecipientsAsync(IEnumerable<Guid> unitIds, Guid campaignId, Guid commandId,
        DateTime selectedAt, string selectedBy, CancellationToken cancellationToken)
    {
        var facts = await CurrentRecipientFactsAsync(unitIds, cancellationToken);
        return facts.Select(f => new CampaignUnit
        {
            TenantId = tenant.TenantId,
            CampaignId = campaignId,
            UnitId = f.UnitId,
            OwnerPartyRef = f.OwnerPartyRef,
            OwnerPartyType = f.OwnerPartyType,
            CustodianPartyRef = f.CustodianPartyRef,
            CustodianPartyType = f.CustodianPartyType,
            SelectedAt = selectedAt,
            SelectedBy = selectedBy,
            SelectionCommandId = commandId,
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> SerialsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var ids in unitIds.Distinct().Chunk(QueryChunk))
        {
            var rows = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && ids.Contains(i.UnitId)
                    && i.ValidTo == null && i.Type == IdentifierType.InternalSerial)
                .Select(i => new { i.UnitId, i.NormalisedValue }).ToListAsync(cancellationToken);
            foreach (var row in rows) result[row.UnitId] = row.NormalisedValue;
        }

        return result;
    }

    private async Task<HashSet<Guid>> IdentifierRangesAsync(IReadOnlyList<CampaignCriterion> ranges, CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>();
        foreach (var range in ranges)
        {
            var rows = await db.Database.SqlQueryRaw<Guid>("""
                SELECT DISTINCT unit_id AS [Value]
                FROM [serialisation].[unit_identifier]
                WHERE tenant_id = @tenant AND valid_to IS NULL AND [type] = @type
                  AND normalised_value COLLATE Latin1_General_100_BIN2 >= @start
                  AND normalised_value COLLATE Latin1_General_100_BIN2 <= @end
                """,
                new SqlParameter("@tenant", tenant.TenantId.Value),
                new SqlParameter("@type", range.IdentifierType!),
                new SqlParameter("@start", range.RangeStart!),
                new SqlParameter("@end", range.RangeEnd!)).ToListAsync(cancellationToken);
            result.UnionWith(rows);
        }

        return result;
    }

    private async Task<HashSet<Guid>> LotsAsync(IReadOnlyList<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        var lots = criteria.Select(c => c.LotId!.Value).Distinct().ToList();
        return (await db.Current.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.LotId != null && lots.Contains(c.LotId.Value))
            .Select(c => c.UnitId).ToListAsync(cancellationToken)).ToHashSet();
    }

    private async Task<HashSet<Guid>> ChildrenAsync(IReadOnlyList<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        var childUnits = criteria.Where(c => c.ChildUnitId != null).Select(c => c.ChildUnitId!.Value).Distinct().ToList();
        var childItems = criteria.Where(c => c.ChildItemId != null).Select(c => c.ChildItemId!.Value).Distinct().ToList();
        var links = db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.RemovedAt == null);
        var rows = await links.Where(l => (l.ChildUnitId != null && childUnits.Contains(l.ChildUnitId.Value))
                || (l.ChildItemId != null && childItems.Contains(l.ChildItemId.Value))
                || (l.ChildUnitId != null && db.Current.Any(c => c.TenantId == tenant.TenantId && c.UnitId == l.ChildUnitId.Value && childItems.Contains(c.ItemId))))
            .Select(l => l.ParentUnitId).Distinct().ToListAsync(cancellationToken);
        return rows.ToHashSet();
    }

    private async Task<HashSet<Guid>> FirmwareAsync(IReadOnlyList<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>();
        foreach (var group in criteria.GroupBy(c => c.EffectiveAt))
        {
            var values = group.Select(c => c.FirmwareValue!).Distinct(StringComparer.Ordinal).ToList();
            result.UnionWith(group.Key is { } at
                ? await FirmwareAtAsync(values, at, cancellationToken)
                : await CurrentFirmwareAsync(values, cancellationToken));
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> CurrentFirmwareAsync(List<string> values, CancellationToken cancellationToken)
    {
        var parameters = ValueParameters(values);
        parameters.Insert(0, new SqlParameter("@tenant", tenant.TenantId.Value));
        var sql = $"""
            SELECT c.unit_id AS [Value]
            FROM [serialisation].[unit_current] c
            CROSS APPLY OPENJSON(c.attributes) a
            WHERE c.tenant_id = @tenant AND a.[key] = N'firmware'
              AND a.[value] COLLATE Latin1_General_100_BIN2 IN ({ValueNames(values.Count)})
            """;
        return await db.Database.SqlQueryRaw<Guid>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> FirmwareAtAsync(List<string> values, DateTime at, CancellationToken cancellationToken)
    {
        var parameters = ValueParameters(values);
        parameters.Insert(0, new SqlParameter("@at", at));
        parameters.Insert(0, new SqlParameter("@tenant", tenant.TenantId.Value));
        var sql = $"""
            WITH firmware_changes AS (
                SELECT e.unit_id, e.event_time, e.recorded_time, e.seq, a.[value] AS firmware
                FROM [serialisation].[unit_event] e
                CROSS APPLY OPENJSON(e.attributes, '$._state.attributes') a
                WHERE e.tenant_id = @tenant AND e.event_time <= @at AND a.[key] = N'firmware'
                UNION ALL
                SELECT e.unit_id, e.event_time, e.recorded_time, e.seq, a.[value] AS firmware
                FROM [serialisation].[unit_event_archive] e
                CROSS APPLY OPENJSON(e.attributes, '$._state.attributes') a
                WHERE e.tenant_id = @tenant AND e.event_time <= @at AND a.[key] = N'firmware'
            ), ranked AS (
                SELECT unit_id, firmware,
                    ROW_NUMBER() OVER (PARTITION BY unit_id ORDER BY event_time DESC, recorded_time DESC, seq DESC) AS rn
                FROM firmware_changes
            )
            SELECT unit_id AS [Value] FROM ranked
            WHERE rn = 1 AND firmware COLLATE Latin1_General_100_BIN2 IN ({ValueNames(values.Count)})
            """;
        return await db.Database.SqlQueryRaw<Guid>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<RecipientFact>> CurrentRecipientFactsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        var result = new List<RecipientFact>();
        foreach (var ids in unitIds.Distinct().Chunk(QueryChunk))
        {
            var current = await db.Current.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && ids.Contains(c.UnitId))
                .Select(c => new { c.UnitId, c.OwnerPartyRef, c.CustodyPartyRef, c.OpenOwnershipEpisodeId, c.OpenCustodyEpisodeId }).ToListAsync(cancellationToken);
            var episodeIds = current.SelectMany(c => new[] { c.OpenOwnershipEpisodeId, c.OpenCustodyEpisodeId }).OfType<Guid>().Distinct().ToList();
            var episodes = episodeIds.Count == 0
                ? new Dictionary<Guid, string?>()
                : await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && episodeIds.Contains(e.EpisodeId))
                    .ToDictionaryAsync(e => e.EpisodeId, e => e.PartyType, cancellationToken);
            var serials = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && ids.Contains(i.UnitId)
                    && i.ValidTo == null && i.Type == IdentifierType.InternalSerial)
                .ToDictionaryAsync(i => i.UnitId, i => i.NormalisedValue, cancellationToken);
            result.AddRange(current.Select(c => new RecipientFact(c.UnitId, serials.GetValueOrDefault(c.UnitId), c.OwnerPartyRef,
                c.OpenOwnershipEpisodeId is { } owner ? episodes.GetValueOrDefault(owner) : null, c.CustodyPartyRef,
                c.OpenCustodyEpisodeId is { } custody ? episodes.GetValueOrDefault(custody) : null)));
        }

        return result;
    }

    private static List<object> ValueParameters(IReadOnlyList<string> values) =>
        values.Select((value, index) => (object)new SqlParameter($"@value{index}", value)).ToList();

    private static string ValueNames(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"@value{i}"));

    private sealed record RecipientFact(Guid UnitId, string? Serial, Guid? OwnerPartyRef, string? OwnerPartyType,
        Guid? CustodianPartyRef, string? CustodianPartyType);
}
