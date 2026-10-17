using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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

    public async Task<IReadOnlyList<CampaignMatchRow>> CurrentMatchesAsync(IEnumerable<Guid> unitIds,
        IReadOnlyList<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        var ids = unitIds.Distinct().ToList();
        var facts = await CurrentRecipientFactsAsync(ids, cancellationToken);
        var childSerials = await MatchedChildSerialsAsync(ids, criteria.Where(c => c.Kind == CampaignCriterionKind.Child).ToList(), cancellationToken);
        var firmware = await FirmwareFactsAsync(ids, criteria.Where(c => c.Kind == CampaignCriterionKind.Firmware).ToList(), cancellationToken);
        return facts.Select(f =>
        {
            var fw = firmware.GetValueOrDefault(f.UnitId);
            return new CampaignMatchRow(f.UnitId, f.Serial, f.ItemId, f.OwnerPartyRef, f.OwnerPartyType,
                f.CustodianPartyRef, f.CustodianPartyType, f.CustodyKind, childSerials.GetValueOrDefault(f.UnitId),
                fw?.Key, fw?.Value, fw?.Since);
        }).ToList();
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
        var result = new HashSet<Guid>();
        foreach (var criterion in criteria)
        {
            var links = db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.RemovedAt == null);
            if (criterion.ChildUnitId is { } childUnit)
            {
                links = links.Where(l => l.ChildUnitId == childUnit);
                if (criterion.ChildLotId is { } lot)
                    links = links.Where(l => db.Current.Any(c => c.TenantId == tenant.TenantId && c.UnitId == l.ChildUnitId && c.LotId == lot));
            }
            else if (criterion.ChildItemId is { } childItem)
            {
                links = links.Where(l => (l.ChildItemId == childItem && (criterion.ChildLotId == null || l.ChildLotId == criterion.ChildLotId))
                    || (l.ChildUnitId != null && db.Current.Any(c => c.TenantId == tenant.TenantId && c.UnitId == l.ChildUnitId
                        && c.ItemId == childItem && (criterion.ChildLotId == null || c.LotId == criterion.ChildLotId))));
            }

            result.UnionWith(await links.Select(l => l.ParentUnitId).Distinct().ToListAsync(cancellationToken));
        }
        return result;
    }

    private async Task<HashSet<Guid>> FirmwareAsync(IReadOnlyList<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>();
        foreach (var group in criteria.GroupBy(c => new { c.FirmwareKey, c.EffectiveAt }))
        {
            var values = group.Select(c => c.FirmwareValue!).Distinct(StringComparer.Ordinal).ToList();
            result.UnionWith(group.Key.EffectiveAt is { } at
                ? await FirmwareAtAsync(group.Key.FirmwareKey!, values, at, cancellationToken)
                : await CurrentFirmwareAsync(group.Key.FirmwareKey!, values, cancellationToken));
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> CurrentFirmwareAsync(string key, List<string> values, CancellationToken cancellationToken)
    {
        var parameters = ValueParameters(values);
        parameters.Insert(0, new SqlParameter("@key", key));
        parameters.Insert(0, new SqlParameter("@tenant", tenant.TenantId.Value));
        var sql = $"""
            SELECT c.unit_id AS [Value]
            FROM [serialisation].[unit_current] c
            CROSS APPLY OPENJSON(c.attributes) a
            WHERE c.tenant_id = @tenant AND a.[key] = @key
              AND a.[value] COLLATE Latin1_General_100_BIN2 IN ({ValueNames(values.Count)})
            """;
        return await db.Database.SqlQueryRaw<Guid>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> FirmwareAtAsync(string key, List<string> values, DateTime at, CancellationToken cancellationToken)
    {
        var parameters = ValueParameters(values);
        parameters.Insert(0, new SqlParameter("@key", key));
        parameters.Insert(0, new SqlParameter("@at", at));
        parameters.Insert(0, new SqlParameter("@tenant", tenant.TenantId.Value));
        var sql = $"""
            WITH firmware_changes AS (
                SELECT h.unit_id, h.effective_from AS event_time, h.effective_from AS recorded_time,
                    CAST(0 AS bigint) AS seq, h.value AS firmware
                FROM [serialisation].[unit_attribute_history] h
                WHERE h.tenant_id = @tenant AND h.effective_from <= @at AND h.[key] = @key
                UNION ALL
                SELECT e.unit_id, e.event_time, e.recorded_time, e.seq, a.[value] AS firmware
                FROM [serialisation].[unit_event] e
                CROSS APPLY OPENJSON(e.attributes, '$._state.attributes') a
                WHERE e.tenant_id = @tenant AND e.event_time <= @at AND a.[key] = @key
                  AND NOT EXISTS (SELECT 1 FROM [serialisation].[unit_attribute_history] h WHERE h.tenant_id = e.tenant_id AND h.event_id = e.event_id AND h.[key] = a.[key] COLLATE DATABASE_DEFAULT)
                UNION ALL
                SELECT e.unit_id, e.event_time, e.recorded_time, e.seq, a.[value] AS firmware
                FROM [serialisation].[unit_event_archive] e
                CROSS APPLY OPENJSON(e.attributes, '$._state.attributes') a
                WHERE e.tenant_id = @tenant AND e.event_time <= @at AND a.[key] = @key
                  AND NOT EXISTS (SELECT 1 FROM [serialisation].[unit_attribute_history] h WHERE h.tenant_id = e.tenant_id AND h.event_id = e.event_id AND h.[key] = a.[key] COLLATE DATABASE_DEFAULT)
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
            // Custody for a recipient is WHERE the unit is (location kind); unit_current.custody_kind only names the episode type.
            var current = await db.Current.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && ids.Contains(c.UnitId))
                .Select(c => new { c.UnitId, c.ItemId, c.OwnerPartyRef, c.CustodyPartyRef, CustodyKind = c.LocationKind, c.OpenOwnershipEpisodeId, c.OpenCustodyEpisodeId }).ToListAsync(cancellationToken);
            var episodeIds = current.SelectMany(c => new[] { c.OpenOwnershipEpisodeId, c.OpenCustodyEpisodeId }).OfType<Guid>().Distinct().ToList();
            var episodes = episodeIds.Count == 0
                ? new Dictionary<Guid, string?>()
                : await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && episodeIds.Contains(e.EpisodeId))
                    .ToDictionaryAsync(e => e.EpisodeId, e => e.PartyType, cancellationToken);
            var serials = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && ids.Contains(i.UnitId)
                    && i.ValidTo == null && i.Type == IdentifierType.InternalSerial)
                .ToDictionaryAsync(i => i.UnitId, i => i.NormalisedValue, cancellationToken);
            result.AddRange(current.Select(c => new RecipientFact(c.UnitId, serials.GetValueOrDefault(c.UnitId), c.ItemId, c.OwnerPartyRef,
                c.OpenOwnershipEpisodeId is { } owner ? episodes.GetValueOrDefault(owner) : null, c.CustodyPartyRef,
                c.OpenCustodyEpisodeId is { } custody ? episodes.GetValueOrDefault(custody) : null, c.CustodyKind)));
        }

        return result;
    }

    private static List<object> ValueParameters(IReadOnlyList<string> values) =>
        values.Select((value, index) => (object)new SqlParameter($"@value{index}", value)).ToList();

    private static string ValueNames(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"@value{i}"));

    private async Task<IReadOnlyDictionary<Guid, string?>> MatchedChildSerialsAsync(List<Guid> unitIds,
        List<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0 || criteria.Count == 0) return new Dictionary<Guid, string?>();
        var links = new List<(Guid ParentUnitId, Guid? ChildUnitId)>();
        foreach (var criterion in criteria)
        {
            var query = db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.RemovedAt == null
                && unitIds.Contains(l.ParentUnitId));
            if (criterion.ChildUnitId is { } childUnit)
            {
                query = query.Where(l => l.ChildUnitId == childUnit);
                if (criterion.ChildLotId is { } lot)
                    query = query.Where(l => db.Current.Any(c => c.TenantId == tenant.TenantId && c.UnitId == l.ChildUnitId && c.LotId == lot));
            }
            else if (criterion.ChildItemId is { } childItem)
            {
                query = query.Where(l => (l.ChildItemId == childItem && (criterion.ChildLotId == null || l.ChildLotId == criterion.ChildLotId))
                    || (l.ChildUnitId != null && db.Current.Any(c => c.TenantId == tenant.TenantId && c.UnitId == l.ChildUnitId
                        && c.ItemId == childItem && (criterion.ChildLotId == null || c.LotId == criterion.ChildLotId))));
            }
            links.AddRange((await query.OrderBy(l => l.InstalledAt).Select(l => new { l.ParentUnitId, l.ChildUnitId })
                .ToListAsync(cancellationToken)).Select(l => (l.ParentUnitId, l.ChildUnitId)));
        }
        var serials = await SerialsAsync(links.Select(l => l.ChildUnitId).OfType<Guid>(), cancellationToken);
        return links.GroupBy(l => l.ParentUnitId).ToDictionary(g => g.Key,
            g => g.Select(l => l.ChildUnitId is { } id ? serials.GetValueOrDefault(id) : null).FirstOrDefault());
    }

    private async Task<IReadOnlyDictionary<Guid, FirmwareFact>> FirmwareFactsAsync(List<Guid> unitIds,
        List<CampaignCriterion> criteria, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0 || criteria.Count == 0) return new Dictionary<Guid, FirmwareFact>();
        var current = await db.Current.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && unitIds.Contains(c.UnitId))
            .Select(c => new { c.UnitId, c.Attributes }).ToListAsync(cancellationToken);
        var facts = new Dictionary<Guid, FirmwareFact>();
        var currentCriteria = criteria.Where(c => c.EffectiveAt == null).GroupBy(c => c.FirmwareKey!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.FirmwareValue!).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var row in current)
        {
            if (string.IsNullOrWhiteSpace(row.Attributes)) continue;
            using var json = JsonDocument.Parse(row.Attributes);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                if (currentCriteria.TryGetValue(property.Name, out var values) && value is not null && values.Contains(value))
                {
                    facts[row.UnitId] = new(property.Name, value, null);
                    break;
                }
            }
        }

        var eventRows = await db.Events.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId) && e.Attributes != null)
            .Select(e => new { e.UnitId, e.EventTime, e.Attributes }).Concat(
                db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId) && e.Attributes != null)
                    .Select(e => new { e.UnitId, e.EventTime, e.Attributes }))
            .OrderByDescending(e => e.EventTime).ToListAsync(cancellationToken);
        var historical = criteria.Where(c => c.EffectiveAt != null)
            .GroupBy(c => new { c.FirmwareKey, c.EffectiveAt })
            .Select(g => new { Key = g.Key.FirmwareKey!, At = g.Key.EffectiveAt!.Value, Values = g.Select(c => c.FirmwareValue!).ToHashSet(StringComparer.Ordinal) })
            .ToList();
        foreach (var unitId in unitIds.Where(id => !facts.ContainsKey(id)))
        {
            foreach (var criterion in historical)
            {
                var change = eventRows.FirstOrDefault(row => row.UnitId == unitId && row.EventTime <= criterion.At
                    && ChangedFirmware(row.Attributes!, criterion.Key) is not null);
                if (change is not null && ChangedFirmware(change.Attributes!, criterion.Key) is { } value && criterion.Values.Contains(value))
                {
                    facts[unitId] = new(criterion.Key, value, change.EventTime);
                    break;
                }
            }
        }
        foreach (var row in eventRows)
        {
            if (!facts.TryGetValue(row.UnitId, out var fact) || fact.Since is not null || row.Attributes is null) continue;
            if (ChangedFirmware(row.Attributes, fact.Key) == fact.Value)
                facts[row.UnitId] = fact with { Since = row.EventTime };
        }
        return facts;
    }

    private static string? ChangedFirmware(string attributes, string key)
    {
        using var json = JsonDocument.Parse(attributes);
        return json.RootElement.TryGetProperty("attribute", out var attribute) && attribute.GetString() == key
            && json.RootElement.TryGetProperty("value", out var value) ? value.ToString() : null;
    }

    private sealed record RecipientFact(Guid UnitId, string? Serial, Guid ItemId, Guid? OwnerPartyRef, string? OwnerPartyType,
        Guid? CustodianPartyRef, string? CustodianPartyType, string? CustodyKind);

    private sealed record FirmwareFact(string Key, string Value, DateTime? Since);
}
