using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public static class EntitlementGapFilterParser
{
    private static readonly HashSet<string> Sorts = ["sale_date_desc", "sale_date_asc", "item", "serial", "channel"];

    public static EntitlementGapFilters Parse(string? items, DateOnly? saleFrom, DateOnly? saleTo, string? channels,
        string? dropShip, string? requirementStatuses, string? sort)
    {
        var itemIds = Split(items).Select(value => Guid.TryParse(value, out var id) ? id : throw new ArgumentException($"'{value}' is not an item id.", nameof(items))).Distinct().ToList();
        var channelList = Split(channels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var statuses = Split(requirementStatuses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var drop = string.IsNullOrWhiteSpace(dropShip) ? EntitlementGapDropShip.Include : dropShip.Trim().ToLowerInvariant();
        if (drop is not (EntitlementGapDropShip.Include or EntitlementGapDropShip.Only or EntitlementGapDropShip.Exclude))
            throw new ArgumentException("dropShip must be include, only or exclude.", nameof(dropShip));
        if (saleFrom > saleTo) throw new ArgumentException("saleFrom must not be after saleTo.", nameof(saleFrom));
        var order = string.IsNullOrWhiteSpace(sort) ? "sale_date_desc" : sort.Trim().ToLowerInvariant();
        if (!Sorts.Contains(order)) throw new ArgumentException($"Sort '{sort}' is not known.", nameof(sort));
        return new(itemIds, saleFrom, saleTo, channelList, drop, statuses, order);
    }

    private static string[] Split(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public static class EntitlementGapCsv
{
    public const string Header = "unit_id,primary_serial,item_id,sale_date,customer_party,channel,drop_ship,requirement_status,missing_input\r\n";

    public static string Row(EntitlementGapRow row) => string.Join(',', new[]
    {
        Cell(row.UnitId.ToString()), Cell(row.PrimarySerial), Cell(row.ItemId.ToString()), Cell(row.SaleDate.ToString("O", CultureInfo.InvariantCulture)),
        Cell(row.CustomerPartyRef.ToString()), Cell(row.Channel), Cell(row.DropShip ? "true" : "false"), Cell(row.RequirementStatus), Cell(row.MissingInput)
    }) + "\r\n";

    public static string Cell(string? value)
    {
        value ??= "";
        // Spreadsheet formula injection: a cell that a spreadsheet would read as a formula (or a tab/CR prefix that hides one) is quoted as text.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

public sealed class EntitlementGapQueries(SerialisationDbContext db, ITenantContext tenant)
{
    private sealed record Gap(Guid UnitId, string? Serial, Guid ItemId, DateTime SaleDate, Guid CustomerPartyRef,
        string Channel, bool DropShip, string? RequirementStatus, string? MissingInput);
    private sealed record PagedGap(Gap Row, int Total);

    public async Task<EntitlementGapPage> PageAsync(EntitlementGapFilters filters, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip); take = Math.Clamp(take, 1, 500);
        var query = ApplyFilters(Query(), filters);
        // Grouping keeps COUNT and the requested page in one SQL statement; no per-unit query or separate count trip.
        var grouped = query.GroupBy(_ => 1);
        var pageQuery = filters.Sort switch
        {
            "sale_date_asc" => grouped.SelectMany(g => g.OrderBy(x => x.SaleDate).ThenBy(x => x.UnitId).Skip(skip).Take(take).Select(x => new PagedGap(x, g.Count()))),
            "item" => grouped.SelectMany(g => g.OrderBy(x => x.ItemId).ThenByDescending(x => x.SaleDate).ThenBy(x => x.UnitId).Skip(skip).Take(take).Select(x => new PagedGap(x, g.Count()))),
            "serial" => grouped.SelectMany(g => g.OrderBy(x => x.Serial).ThenBy(x => x.UnitId).Skip(skip).Take(take).Select(x => new PagedGap(x, g.Count()))),
            "channel" => grouped.SelectMany(g => g.OrderBy(x => x.Channel).ThenByDescending(x => x.SaleDate).ThenBy(x => x.UnitId).Skip(skip).Take(take).Select(x => new PagedGap(x, g.Count()))),
            _ => grouped.SelectMany(g => g.OrderByDescending(x => x.SaleDate).ThenBy(x => x.UnitId).Skip(skip).Take(take).Select(x => new PagedGap(x, g.Count()))),
        };
        var page = await pageQuery.ToListAsync(cancellationToken);
        var rows = page.Select(x => new EntitlementGapRow(x.Row.UnitId, x.Row.Serial, x.Row.ItemId, x.Row.SaleDate,
            x.Row.CustomerPartyRef, x.Row.Channel, x.Row.DropShip, x.Row.RequirementStatus, x.Row.MissingInput)).ToList();
        return new(rows, page.FirstOrDefault()?.Total ?? 0, skip, take);
    }

    public async Task<IReadOnlyList<EntitlementGapCount>> CountsAsync(EntitlementGapFilters filters, CancellationToken cancellationToken) =>
        await ApplyFilters(Query(), filters).GroupBy(x => new { x.ItemId, x.Channel, x.DropShip })
            .Select(g => new EntitlementGapCount(g.Key.ItemId, g.Key.Channel, g.Key.DropShip, g.Count()))
            .OrderBy(x => x.ItemId).ThenBy(x => x.Channel).ThenBy(x => x.DropShip).ToListAsync(cancellationToken);

    public async Task<EntitlementGapRunResult?> LastRunAsync(CancellationToken cancellationToken)
    {
        var row = await db.EntitlementGapRuns.AsNoTracking().Where(r => r.TenantId == tenant.TenantId)
            .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new(row.RunId, row.RunAt, row.Trigger, row.Total,
            JsonSerializer.Deserialize<List<EntitlementGapCount>>(row.CountsJson, LedgerJson.Options) ?? []);
    }

    private IQueryable<Gap> Query()
    {
        var tenantId = tenant.TenantId;
        return from episode in db.Episodes.AsNoTracking()
               join current in db.Current.AsNoTracking() on new { episode.TenantId, episode.UnitId } equals new { current.TenantId, current.UnitId }
               join unit in db.Units.AsNoTracking() on new { episode.TenantId, episode.UnitId } equals new { unit.TenantId, unit.UnitId }
               where episode.TenantId == tenantId && episode.Kind == EpisodeKind.Ownership && episode.PartyType == PartyType.Customer && episode.PartyRef != null
                   && !db.Episodes.Any(later => later.TenantId == tenantId && later.UnitId == episode.UnitId && later.Kind == EpisodeKind.Ownership
                       && later.PartyType == PartyType.Customer && (later.ValidFrom > episode.ValidFrom || later.ValidFrom == episode.ValidFrom && later.EpisodeId.CompareTo(episode.EpisodeId) > 0))
                   // The ADR says live or expired cover. Void/correction/included-in-error closures never establish valid cover.
                   && !db.Entitlements.Any(e => e.TenantId == tenantId && e.UnitId == episode.UnitId && e.CoverageFamily == CoverageFamilyCode.Base
                       && (e.Status != EntitlementStatus.Closed || e.ClosedReason == EntitlementClosedReason.Expired))
               let serial = db.Identifiers.Where(i => i.TenantId == tenantId && i.UnitId == episode.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null)
                   .Select(i => i.RawValue).FirstOrDefault()
               let requirement = db.EntitlementRequirements.Where(r => r.TenantId == tenantId && r.UnitId == episode.UnitId && r.OwnershipEpisodeId == episode.EpisodeId)
                   .OrderByDescending(r => r.RaisedAt).FirstOrDefault()
               let channel = episode.Source ?? episode.OpeningDocumentType ?? "unknown"
               let dropShip = unit.Origin == UnitOrigin.VendorAsn || episode.OpeningDocumentType == BackfillStartBasis.VendorShipConfirmation
               select new Gap(episode.UnitId, serial, current.ItemId, episode.ValidFrom, episode.PartyRef!.Value, channel, dropShip,
                   requirement == null ? null : requirement.Status, requirement == null ? null : requirement.MissingInput);
    }

    private static IQueryable<Gap> ApplyFilters(IQueryable<Gap> query, EntitlementGapFilters filters)
    {
        if (filters.ItemIds is { Count: > 0 }) query = query.Where(x => filters.ItemIds.Contains(x.ItemId));
        if (filters.SaleFrom is { } from) { var at = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); query = query.Where(x => x.SaleDate >= at); }
        if (filters.SaleTo is { } to) { var before = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); query = query.Where(x => x.SaleDate < before); }
        if (filters.Channels is { Count: > 0 }) query = query.Where(x => filters.Channels.Contains(x.Channel));
        if (filters.DropShip == EntitlementGapDropShip.Only) query = query.Where(x => x.DropShip);
        else if (filters.DropShip == EntitlementGapDropShip.Exclude) query = query.Where(x => !x.DropShip);
        if (filters.RequirementStatuses is { Count: > 0 }) query = query.Where(x => x.RequirementStatus != null && filters.RequirementStatuses.Contains(x.RequirementStatus));
        return query;
    }

}

public static class RunEntitlementGapReportHandler
{
    public static async Task<EntitlementGapRunResult> Handle(RunEntitlementGapReport command, EntitlementGapQueries queries,
        SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (command.Trigger is not (EntitlementGapTrigger.MigrationLoad or EntitlementGapTrigger.OnDemand or EntitlementGapTrigger.DailyJob))
            throw new CommandRefusedException("trigger_unknown", $"Entitlement gap trigger '{command.Trigger}' is not known.");
        var existing = await db.EntitlementGapRuns.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RunId == command.CommandId, cancellationToken);
        if (existing is not null) return Result(existing);
        var counts = await queries.CountsAsync(command.Filters, cancellationToken);
        var row = new EntitlementGapRun { TenantId = tenant.TenantId, RunId = command.CommandId, RunAt = clock.UtcNow, Trigger = command.Trigger,
            FiltersJson = JsonSerializer.Serialize(command.Filters, LedgerJson.Options), Total = counts.Sum(x => x.Count), CountsJson = JsonSerializer.Serialize(counts, LedgerJson.Options) };
        db.EntitlementGapRuns.Add(row);
        return new(row.RunId, row.RunAt, row.Trigger, row.Total, counts);
    }

    private static EntitlementGapRunResult Result(EntitlementGapRun row) => new(row.RunId, row.RunAt, row.Trigger, row.Total,
        JsonSerializer.Deserialize<List<EntitlementGapCount>>(row.CountsJson, LedgerJson.Options) ?? []);
}
