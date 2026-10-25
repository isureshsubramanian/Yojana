using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public static class EntitlementGapFilterParser
{
    public static EntitlementGapFilters Parse(string? items, DateOnly? saleFrom, DateOnly? saleTo, string? channels,
        string? dropShip, string? requirementStatuses, string? sort, string? direction = null)
    {
        var itemIds = Split(items).Select(value => Guid.TryParse(value, out var id) ? id : throw new ArgumentException($"'{value}' is not an item id.", nameof(items))).Distinct().ToList();
        var channelList = Split(channels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var statuses = Split(requirementStatuses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var drop = string.IsNullOrWhiteSpace(dropShip) ? EntitlementGapDropShip.Include : dropShip.Trim().ToLowerInvariant();
        if (drop is not (EntitlementGapDropShip.Include or EntitlementGapDropShip.Only or EntitlementGapDropShip.Exclude))
            throw new ArgumentException("dropShip must be include, only or exclude.", nameof(dropShip));
        if (saleFrom > saleTo) throw new ArgumentException("saleFrom must not be after saleTo.", nameof(saleFrom));
        return new(itemIds, saleFrom, saleTo, channelList, drop, statuses,
            string.IsNullOrWhiteSpace(sort) ? "sale_date" : sort.Trim().ToLowerInvariant(), direction ?? "desc");
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
    // Built by member initialisation, not a constructor: EF translates filters and ordering on members it assigned itself.
    private sealed class Gap
    {
        public Guid UnitId { get; init; }
        public string? Serial { get; init; }
        public Guid ItemId { get; init; }
        public DateTime SaleDate { get; init; }
        public Guid CustomerPartyRef { get; init; }
        public required string Channel { get; init; }
        public bool DropShip { get; init; }
        public string? RequirementStatus { get; init; }
        public string? MissingInput { get; init; }
    }

    private static readonly SortedPage<Gap, Guid> Pages = new("sale_date", row => row.UnitId,
        new SortColumn<Gap, DateTime>("sale_date", row => row.SaleDate, SortDirection.Descending),
        new SortColumn<Gap, string>("serial", row => row.Serial ?? ""),
        new SortColumn<Gap, string>("channel", row => row.Channel));

    public async Task<EntitlementGapPage> PageAsync(EntitlementGapFilters filters, int skip, int take, CancellationToken cancellationToken)
    {
        var paging = Pages.Parse(filters.Sort, filters.Direction, take, skip: skip);
        var query = ApplyFilters(Query(), filters);
        // Two statements: the count, then the page. Both are set-based; there is no per-unit query.
        var total = await query.CountAsync(cancellationToken);
        var page = await paging.Apply(query).Take(paging.Take).ToListAsync(cancellationToken);
        var rows = page.Select(x => new EntitlementGapRow(x.UnitId, x.Serial, x.ItemId, x.SaleDate,
            x.CustomerPartyRef, x.Channel, x.DropShip, x.RequirementStatus, x.MissingInput)).ToList();
        return new(rows, total, paging.Skip, paging.Take);
    }

    public async Task<IReadOnlyList<EntitlementGapCount>> CountsAsync(EntitlementGapFilters filters, CancellationToken cancellationToken) =>
        (await ApplyFilters(Query(), filters).GroupBy(x => new { x.ItemId, x.Channel, x.DropShip })
            .Select(g => new { g.Key.ItemId, g.Key.Channel, g.Key.DropShip, Count = g.Count() })
            .OrderBy(x => x.ItemId).ThenBy(x => x.Channel).ThenBy(x => x.DropShip).ToListAsync(cancellationToken))
        .Select(x => new EntitlementGapCount(x.ItemId, x.Channel, x.DropShip, x.Count)).ToList();

    public async Task<EntitlementGapRunResult?> LastRunAsync(CancellationToken cancellationToken)
    {
        var row = await db.EntitlementGapRuns.AsNoTracking().Where(r => r.TenantId == tenant.TenantId)
            .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new(row.RunId, row.RunAt, row.RunTrigger, row.Total,
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
                       && later.PartyType == PartyType.Customer && later.ValidFrom > episode.ValidFrom)
                   // The ADR says live or expired cover. Void/correction/included-in-error closures never establish valid cover.
                   && !db.Entitlements.Any(e => e.TenantId == tenantId && e.UnitId == episode.UnitId && e.CoverageFamily == CoverageFamilyCode.Base
                       && (e.Status != EntitlementStatus.Closed || e.ClosedReason == EntitlementClosedReason.Expired))
               let serial = db.Identifiers.Where(i => i.TenantId == tenantId && i.UnitId == episode.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null)
                   .Select(i => i.RawValue).FirstOrDefault()
               let requirement = db.EntitlementRequirements.Where(r => r.TenantId == tenantId && r.UnitId == episode.UnitId && r.OwnershipEpisodeId == episode.EpisodeId)
                   .OrderByDescending(r => r.RaisedAt).FirstOrDefault()
               let channel = episode.Source ?? episode.OpeningDocumentType ?? "unknown"
               let dropShip = unit.Origin == UnitOrigin.VendorAsn || episode.OpeningDocumentType == BackfillStartBasis.VendorShipConfirmation
               select new Gap
               {
                   UnitId = episode.UnitId, Serial = serial, ItemId = current.ItemId, SaleDate = episode.ValidFrom, CustomerPartyRef = episode.PartyRef!.Value, Channel = channel, DropShip = dropShip,
                   RequirementStatus = requirement == null ? null : requirement.Status, MissingInput = requirement == null ? null : requirement.MissingInput,
               };
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
        var row = new EntitlementGapRun { TenantId = tenant.TenantId, RunId = command.CommandId, RunAt = clock.UtcNow, RunTrigger = command.Trigger,
            FiltersJson = JsonSerializer.Serialize(command.Filters, LedgerJson.Options), Total = counts.Sum(x => x.Count), CountsJson = JsonSerializer.Serialize(counts, LedgerJson.Options) };
        db.EntitlementGapRuns.Add(row);
        return new(row.RunId, row.RunAt, row.RunTrigger, row.Total, counts);
    }

    private static EntitlementGapRunResult Result(EntitlementGapRun row) => new(row.RunId, row.RunAt, row.RunTrigger, row.Total,
        JsonSerializer.Deserialize<List<EntitlementGapCount>>(row.CountsJson, LedgerJson.Options) ?? []);
}
