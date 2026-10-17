using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Queries;

public static class CorrectionKpiRules
{
    public const int BaselineDays = 90;

    public static string MonthBucket(int year, int month) => $"{year:D4}-{month:D2}";

    public static CorrectionKpiBaseline Baseline(DateOnly? startedOn, DateOnly asOf)
    {
        var days = startedOn is null ? 0 : Math.Max(0, asOf.DayNumber - startedOn.Value.DayNumber);
        var remaining = Math.Max(0, BaselineDays - days);
        return new(remaining == 0 ? CorrectionKpiStatus.Ready : CorrectionKpiStatus.BaselineBuilding, days, remaining,
            startedOn, startedOn?.AddDays(BaselineDays));
    }

    public static IReadOnlyList<string> Validate(CorrectionKpiTargetPolicy policy)
    {
        var problems = new List<string>();
        if (policy.RootCauses.Select(t => t.RootCauseCategory).Distinct().Count() != policy.RootCauses.Count) problems.Add("a root cause has more than one target");
        if (policy.RootCauses.Any(t => t.RootCauseCategory is < 1 or > 7)) problems.Add("root-cause categories must be between 1 and 7");
        if (policy.RootCauses.Any(t => t.MaximumPerThousand < 0)) problems.Add("targets cannot be negative");
        return problems;
    }
}

/// <summary>Tenant-owned KPI targets under a module-specific platform setting key.</summary>
public sealed class CorrectionKpiTargetSettings(ITenantSettings settings)
{
    public const string Key = "corrections.kpi_targets";
    public static readonly CorrectionKpiTargetPolicy Default = new([]);

    public async Task<CorrectionKpiTargetPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<CorrectionKpiTargetPolicy>(text, LedgerJson.Options) ?? Default
            : Default;

    public async Task SetAsync(CorrectionKpiTargetPolicy policy, CancellationToken cancellationToken)
    {
        if (CorrectionKpiRules.Validate(policy) is { Count: > 0 } problems)
            throw new CommandRefusedException("kpi_targets_invalid", string.Join("; ", problems));
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }
}

public static class CorrectionKpiCsv
{
    public const string Header = "month,breakdown,dimension,corrections,units_corrected,units_shipped,per_1000,reversed_later,target_per_1000\r\n";

    public static string Row(string breakdown, CorrectionKpiRow row) => string.Join(',',
    [
        Cell(row.Bucket), Cell(breakdown), Cell(row.Dimension), row.Corrections.ToString(CultureInfo.InvariantCulture),
        row.UnitsCorrected.ToString(CultureInfo.InvariantCulture), row.UnitsShipped.ToString(CultureInfo.InvariantCulture),
        row.PerThousand?.ToString("0.00", CultureInfo.InvariantCulture) ?? "", row.ReversedLater.ToString(CultureInfo.InvariantCulture),
        row.TargetPerThousand?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
    ]) + "\r\n";

    public static string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>Set-based correction-volume aggregates over the indexed projection; no correction row is loaded individually.</summary>
public sealed class CorrectionKpiQueries(SerialisationDbContext db, ITenantContext tenant, IClock clock, CorrectionKpiTargetSettings targets)
{
    private sealed class Aggregate
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public required string Dimension { get; init; }
        public int Corrections { get; init; }
        public int Units { get; init; }
        public int Reversed { get; init; }
    }

    private sealed class NumericAggregate
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public int Dimension { get; init; }
        public int Corrections { get; init; }
        public int Units { get; init; }
        public int Reversed { get; init; }
    }

    private sealed class ShippedAggregate
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public int Units { get; init; }
    }

    public async Task<CorrectionKpiReport> QueryAsync(DateOnly from, DateOnly to, string breakdown, Guid? itemId, CancellationToken cancellationToken)
    {
        if (from > to) throw new ArgumentException("from must not be after to.", nameof(from));
        if (!CorrectionKpiBreakdown.All.Contains(breakdown)) throw new ArgumentException($"Breakdown '{breakdown}' is not known.", nameof(breakdown));
        var start = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var tenantId = tenant.TenantId;
        var source = db.Corrections.AsNoTracking().Where(c => c.TenantId == tenantId && c.EventTime >= start && c.EventTime < end)
            // P3-T16b writes request rows with no root cause. Open/no-command rows are the unclassified line; once
            // re-filed, that request row is excluded and its resulting correction supplies the single classified count.
            .Where(c => c.RootCauseCategory != null || (c.RequestStatus != null && c.RequestStatus != CorrectionRequestStatus.Refiled));
        if (itemId is { } item)
            source = source.Where(c => db.CorrectionUnits.Any(link => link.TenantId == tenantId && link.CorrectionId == c.CorrectionId && link.ItemId == item));

        var aggregates = breakdown switch
        {
            CorrectionKpiBreakdown.RootCause => await RootCauseAsync(source, tenantId, cancellationToken),
            CorrectionKpiBreakdown.InitiatingRole => await TextAsync(source, tenantId, CorrectionKpiBreakdown.InitiatingRole, cancellationToken),
            CorrectionKpiBreakdown.InitiatedVia => await TextAsync(source, tenantId, CorrectionKpiBreakdown.InitiatedVia, cancellationToken),
            CorrectionKpiBreakdown.ReversedLater => await ReversedAsync(source, tenantId, cancellationToken),
            CorrectionKpiBreakdown.Item => await ItemAsync(source, tenantId, cancellationToken),
            _ => [],
        };

        var shipped = await ShippedAsync(start, end, tenantId, cancellationToken);
        var first = await db.Corrections.AsNoTracking().Where(c => c.TenantId == tenantId).MinAsync(c => (DateTime?)c.RecordedTime, cancellationToken);
        var baseline = CorrectionKpiRules.Baseline(first is null ? null : DateOnly.FromDateTime(first.Value), DateOnly.FromDateTime(clock.UtcNow));
        var policy = baseline.Status == CorrectionKpiStatus.Ready ? await targets.GetAsync(cancellationToken) : CorrectionKpiTargetSettings.Default;
        var targetByCause = policy.RootCauses.ToDictionary(t => t.RootCauseCategory, t => t.MaximumPerThousand);
        var rows = aggregates.OrderBy(a => a.Year).ThenBy(a => a.Month).ThenBy(a => a.Dimension, StringComparer.Ordinal).Select(a =>
        {
            var shippedUnits = shipped.GetValueOrDefault((a.Year, a.Month));
            decimal? rate = shippedUnits == 0 ? null : Math.Round(a.Corrections * 1000m / shippedUnits, 2, MidpointRounding.AwayFromZero);
            decimal? target = breakdown == CorrectionKpiBreakdown.RootCause && int.TryParse(a.Dimension, out var category)
                ? targetByCause.GetValueOrDefault(category)
                : null;
            return new CorrectionKpiRow(CorrectionKpiRules.MonthBucket(a.Year, a.Month), a.Dimension, a.Corrections, a.Units, shippedUnits, rate, a.Reversed, target);
        }).ToList();
        return new(from, to, breakdown, itemId, baseline, rows);
    }

    private async Task<List<Aggregate>> RootCauseAsync(IQueryable<CorrectionRecord> source, TenantId tenantId, CancellationToken cancellationToken)
    {
        var rows = await source.GroupBy(c => new { c.EventTime.Year, c.EventTime.Month, Dimension = c.RootCauseCategory ?? 0 })
            .Select(g => new NumericAggregate
            {
                Year = g.Key.Year, Month = g.Key.Month, Dimension = g.Key.Dimension, Corrections = g.Count(), Units = g.Sum(c => c.UnitCount),
                Reversed = g.Count(c => db.Corrections.Any(later => later.TenantId == tenantId && later.CorrectsCorrectionId == c.CorrectionId)),
            }).ToListAsync(cancellationToken);
        return rows.Select(r => new Aggregate { Year = r.Year, Month = r.Month, Dimension = r.Dimension == 0 ? "unclassified" : r.Dimension.ToString(CultureInfo.InvariantCulture), Corrections = r.Corrections, Units = r.Units, Reversed = r.Reversed }).ToList();
    }

    private Task<List<Aggregate>> TextAsync(IQueryable<CorrectionRecord> source, TenantId tenantId, string breakdown, CancellationToken cancellationToken) =>
        (breakdown == CorrectionKpiBreakdown.InitiatingRole ? source.GroupBy(c => new { c.EventTime.Year, c.EventTime.Month, Dimension = c.InitiatingRole })
            : source.GroupBy(c => new { c.EventTime.Year, c.EventTime.Month, Dimension = c.InitiatedVia }))
        .Select(g => new Aggregate
        {
            Year = g.Key.Year, Month = g.Key.Month, Dimension = g.Key.Dimension, Corrections = g.Count(), Units = g.Sum(c => c.UnitCount),
            Reversed = g.Count(c => db.Corrections.Any(later => later.TenantId == tenantId && later.CorrectsCorrectionId == c.CorrectionId)),
        }).ToListAsync(cancellationToken);

    private async Task<List<Aggregate>> ReversedAsync(IQueryable<CorrectionRecord> source, TenantId tenantId, CancellationToken cancellationToken)
    {
        var rows = await source.GroupBy(c => new { c.EventTime.Year, c.EventTime.Month, Dimension = db.Corrections.Any(later => later.TenantId == tenantId && later.CorrectsCorrectionId == c.CorrectionId) })
            .Select(g => new NumericAggregate { Year = g.Key.Year, Month = g.Key.Month, Dimension = g.Key.Dimension ? 1 : 0, Corrections = g.Count(), Units = g.Sum(c => c.UnitCount), Reversed = g.Key.Dimension ? g.Count() : 0 })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new Aggregate { Year = r.Year, Month = r.Month, Dimension = r.Dimension == 1 ? "yes" : "no", Corrections = r.Corrections, Units = r.Units, Reversed = r.Reversed }).ToList();
    }

    private async Task<List<Aggregate>> ItemAsync(IQueryable<CorrectionRecord> source, TenantId tenantId, CancellationToken cancellationToken)
    {
        var query = from link in db.CorrectionUnits.AsNoTracking()
                    join correction in source on new { link.TenantId, link.CorrectionId } equals new { correction.TenantId, correction.CorrectionId }
                    group new { link, correction } by new { correction.EventTime.Year, correction.EventTime.Month, link.ItemId } into g
                    select new Aggregate
                    {
                        Year = g.Key.Year, Month = g.Key.Month, Dimension = g.Key.ItemId.ToString(),
                        Corrections = g.Select(x => x.correction.CorrectionId).Distinct().Count(), Units = g.Count(),
                        Reversed = g.Where(x => db.Corrections.Any(later => later.TenantId == tenantId && later.CorrectsCorrectionId == x.correction.CorrectionId)).Select(x => x.correction.CorrectionId).Distinct().Count(),
                    };
        return await query.ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<(int Year, int Month), int>> ShippedAsync(DateTime start, DateTime end, TenantId tenantId, CancellationToken cancellationToken)
    {
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.EventType == UnitEventType.Shipped && e.EventTime >= start && e.EventTime < end)
            .GroupBy(e => new { e.EventTime.Year, e.EventTime.Month }).Select(g => new ShippedAggregate { Year = g.Key.Year, Month = g.Key.Month, Units = g.Select(e => e.UnitId).Distinct().Count() }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.EventType == UnitEventType.Shipped && e.EventTime >= start && e.EventTime < end)
            .GroupBy(e => new { e.EventTime.Year, e.EventTime.Month }).Select(g => new ShippedAggregate { Year = g.Key.Year, Month = g.Key.Month, Units = g.Select(e => e.UnitId).Distinct().Count() }).ToListAsync(cancellationToken);
        return live.Concat(archived).GroupBy(x => (x.Year, x.Month)).ToDictionary(g => g.Key, g => g.Sum(x => x.Units));
    }
}
