using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims.Risk;

public sealed class ClaimRiskQueries(SerialisationDbContext db, IUserDirectory users, ITenantContext tenant)
{
    public async Task<IReadOnlyList<ClaimRiskFlagRow>> ForClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = await (from flag in db.ClaimRiskFlags.AsNoTracking()
            join claim in db.Claims.AsNoTracking() on new { flag.TenantId, flag.ClaimId } equals new { claim.TenantId, claim.ClaimId }
            where flag.TenantId == tenantId && flag.ClaimId == claimId
            orderby flag.RaisedAt, flag.FlagId
            select new FlagProjection { Flag = flag, ClaimNumber = claim.ClaimNumber }).ToListAsync(cancellationToken);
        return await RowsAsync(rows, cancellationToken);
    }

    public async Task<ClaimRiskFlagPage> ListAsync(string? status, string? rule, DateOnly? from, DateOnly? to,
        string? cursor, int take, CancellationToken cancellationToken)
    {
        ValidateFilters(status, rule, from, to);
        take = Math.Clamp(take, 1, 200);
        var tenantId = tenant.TenantId;
        var query = from flag in db.ClaimRiskFlags.AsNoTracking()
                    join claim in db.Claims.AsNoTracking()
                        on new { flag.TenantId, flag.ClaimId } equals new { claim.TenantId, claim.ClaimId }
                    where flag.TenantId == tenantId
                    select new FlagProjection { Flag = flag, ClaimNumber = claim.ClaimNumber };
        if (status == "open") query = query.Where(row => row.Flag.Status == ClaimRiskFlagStatus.Open);
        else if (status == "decided") query = query.Where(row => row.Flag.Status != ClaimRiskFlagStatus.Open);
        if (!string.IsNullOrWhiteSpace(rule)) query = query.Where(row => row.Flag.RuleCode == rule);
        if (from is { } fromDate)
        {
            var at = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(row => row.Flag.RaisedAt >= at);
        }
        if (to is { } toDate)
        {
            var before = toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(row => row.Flag.RaisedAt < before);
        }
        if (Decode(cursor) is { } after)
            query = query.Where(row => row.Flag.RaisedAt > after.RaisedAt
                || row.Flag.RaisedAt == after.RaisedAt && row.Flag.FlagId.CompareTo(after.FlagId) > 0);

        var bounded = await query.OrderBy(row => row.Flag.RaisedAt).ThenBy(row => row.Flag.FlagId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = bounded.Count > take;
        var page = bounded.Take(take).ToList();
        var result = await RowsAsync(page, cancellationToken);
        var next = hasMore && page.Count > 0 ? Encode(page[^1].Flag.RaisedAt, page[^1].Flag.FlagId) : null;
        return new(result, next, hasMore, take);
    }

    public async Task<ClaimRiskReviewPage> ReviewAsync(string? status, string? rule, DateOnly? from, DateOnly? to,
        string? cursor, int take, CancellationToken cancellationToken)
    {
        ValidateFilters(status, rule, from, to);
        take = Math.Clamp(take, 1, 200);
        var tenantId = tenant.TenantId;
        var matching = db.ClaimRiskFlags.AsNoTracking().Where(flag => flag.TenantId == tenantId);
        if (status == "open") matching = matching.Where(flag => flag.Status == ClaimRiskFlagStatus.Open);
        else if (status == "decided") matching = matching.Where(flag => flag.Status != ClaimRiskFlagStatus.Open);
        if (!string.IsNullOrWhiteSpace(rule)) matching = matching.Where(flag => flag.RuleCode == rule);
        if (from is { } fromDate)
        {
            var at = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            matching = matching.Where(flag => flag.RaisedAt >= at);
        }
        if (to is { } toDate)
        {
            var before = toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            matching = matching.Where(flag => flag.RaisedAt < before);
        }

        var keys = matching.GroupBy(flag => flag.ClaimId).Select(group => new ReviewKeyProjection
        {
            ClaimId = group.Key,
            FirstRaisedAt = group.Min(flag => flag.RaisedAt),
        });
        if (Decode(cursor) is { } after)
            keys = keys.Where(row => row.FirstRaisedAt > after.RaisedAt
                || row.FirstRaisedAt == after.RaisedAt && row.ClaimId.CompareTo(after.FlagId) > 0);

        var bounded = await keys.OrderBy(row => row.FirstRaisedAt).ThenBy(row => row.ClaimId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = bounded.Count > take;
        var pageKeys = bounded.Take(take).ToList();
        if (pageKeys.Count == 0) return new([], null, false, take);

        var claimIds = pageKeys.Select(row => row.ClaimId).ToList();
        var claims = await db.Claims.AsNoTracking().Where(claim => claim.TenantId == tenantId && claimIds.Contains(claim.ClaimId))
            .ToDictionaryAsync(claim => claim.ClaimId, cancellationToken);
        var lineTotals = await db.ClaimLines.AsNoTracking()
            .Where(line => line.TenantId == tenantId && claimIds.Contains(line.ClaimId))
            .GroupBy(line => line.ClaimId).Select(group => new ClaimTotalProjection
            {
                ClaimId = group.Key,
                RequestedAmount = group.Sum(line => line.RequestedAmount),
            }).ToDictionaryAsync(row => row.ClaimId, cancellationToken);
        var allFlags = await db.ClaimRiskFlags.AsNoTracking()
            .Where(flag => flag.TenantId == tenantId && claimIds.Contains(flag.ClaimId))
            .OrderBy(flag => flag.RaisedAt).ThenBy(flag => flag.FlagId).ToListAsync(cancellationToken);
        var flagsByClaim = allFlags.GroupBy(flag => flag.ClaimId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<ClaimRiskReviewFlag>)group.Select(flag =>
                new ClaimRiskReviewFlag(flag.FlagId, flag.RuleCode, flag.Status, flag.RaisedAt)).ToList());
        var unitIds = claims.Values.Select(claim => claim.UnitId).Distinct().ToList();
        var itemIds = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var serials = await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken);

        var rows = pageKeys.Select(key =>
        {
            var claim = claims[key.ClaimId];
            return new ClaimRiskReviewRow(claim.ClaimId, claim.ClaimNumber, claim.UnitId,
                serials.GetValueOrDefault(claim.UnitId), itemIds.GetValueOrDefault(claim.UnitId),
                claim.ClaimantPartyRef, claim.Remedy, claim.Currency,
                lineTotals.GetValueOrDefault(claim.ClaimId)?.RequestedAmount ?? 0m,
                flagsByClaim.GetValueOrDefault(claim.ClaimId) ?? [], key.FirstRaisedAt);
        }).ToList();
        var next = hasMore ? Encode(pageKeys[^1].FirstRaisedAt, pageKeys[^1].ClaimId) : null;
        return new(rows, next, hasMore, take);
    }

    public async Task<ClaimRiskReviewSummary> SummaryAsync(DateOnly from, DateOnly to,
        CancellationToken cancellationToken)
    {
        if (from > to)
            throw new CommandRefusedException("claim_risk_summary_period_invalid", "The summary from date cannot be after the to date.");
        var tenantId = tenant.TenantId;
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var open = await db.ClaimRiskFlags.AsNoTracking()
            .Where(flag => flag.TenantId == tenantId && flag.Status == ClaimRiskFlagStatus.Open)
            .GroupBy(_ => 1).Select(group => new OpenSummaryProjection
            {
                HeldClaims = group.Select(flag => flag.ClaimId).Distinct().Count(),
                OpenFlags = group.Count(),
                OldestRaisedAt = group.Min(flag => flag.RaisedAt),
            }).SingleOrDefaultAsync(cancellationToken);
        var submitted = await db.Claims.AsNoTracking().CountAsync(claim => claim.TenantId == tenantId
            && claim.SubmittedAt >= start && claim.SubmittedAt < end, cancellationToken);
        var period = await db.ClaimRiskFlags.AsNoTracking()
            .Where(flag => flag.TenantId == tenantId && flag.RaisedAt >= start && flag.RaisedAt < end)
            .GroupBy(_ => 1).Select(group => new PeriodSummaryProjection
            {
                ClaimsFlagged = group.Select(flag => flag.ClaimId).Distinct().Count(),
                FlagsRaised = group.Count(),
                FlagsConfirmed = group.Count(flag => flag.Status == ClaimRiskFlagStatus.Confirmed),
            }).SingleOrDefaultAsync(cancellationToken);
        var claimsDenied = await (from flag in db.ClaimRiskFlags.AsNoTracking()
            join claim in db.Claims.AsNoTracking()
                on new { flag.TenantId, flag.ClaimId } equals new { claim.TenantId, claim.ClaimId }
            where flag.TenantId == tenantId && flag.RaisedAt >= start && flag.RaisedAt < end
                && flag.Status == ClaimRiskFlagStatus.Confirmed && flag.DecidedAt != null
                && claim.Status == ClaimStatus.Denied && claim.AdjudicatedAt >= flag.DecidedAt
            select claim.ClaimId).Distinct().CountAsync(cancellationToken);

        // SQL Server exposes the continuous percentile as a window function. Keeping it here makes the duration
        // median one set-based database calculation; the explicit tenant predicate remains required even with RLS.
        var median = await db.Database.SqlQuery<decimal?>($"""
            SELECT TOP(1) CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (
                ORDER BY DATEDIFF_BIG(MILLISECOND, raised_at, decided_at) / 3600000.0
            ) OVER () AS decimal(18, 4)) AS [Value]
            FROM [serialisation].[claim_risk_flag]
            WHERE tenant_id = {tenantId.Value}
              AND decided_at >= {start} AND decided_at < {end}
            """).SingleOrDefaultAsync(cancellationToken);

        var flagsRaised = period?.FlagsRaised ?? 0;
        var flagsConfirmed = period?.FlagsConfirmed ?? 0;
        return new(open?.HeldClaims ?? 0, open?.OpenFlags ?? 0, open?.OldestRaisedAt,
            submitted, period?.ClaimsFlagged ?? 0,
            submitted == 0 ? 0m : decimal.Round(decimal.Divide(period?.ClaimsFlagged ?? 0, submitted), 4),
            flagsRaised, flagsConfirmed,
            flagsRaised == 0 ? 0m : decimal.Round(decimal.Divide(flagsConfirmed, flagsRaised), 4),
            claimsDenied, median);
    }

    public async Task<IReadOnlyList<ClaimRiskFlagReportRow>> ReportAsync(DateOnly from, DateOnly to,
        CancellationToken cancellationToken)
    {
        if (from > to) throw new CommandRefusedException("claim_risk_report_period_invalid", "The report from date cannot be after the to date.");
        var tenantId = tenant.TenantId;
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var aggregates = await (from flag in db.ClaimRiskFlags.AsNoTracking()
            join claim in db.Claims.AsNoTracking() on new { flag.TenantId, flag.ClaimId } equals new { claim.TenantId, claim.ClaimId }
            where flag.TenantId == tenantId && flag.RaisedAt >= start && flag.RaisedAt < end
            group new { flag, claim } by flag.RuleCode into grouped
            select new ReportProjection
            {
                RuleCode = grouped.Key,
                Hits = grouped.Count(),
                Cleared = grouped.Count(row => row.flag.Status == ClaimRiskFlagStatus.Cleared),
                Confirmed = grouped.Count(row => row.flag.Status == ClaimRiskFlagStatus.Confirmed),
                ConfirmedDenied = grouped.Count(row => row.flag.Status == ClaimRiskFlagStatus.Confirmed
                    && row.flag.DecidedAt != null && row.claim.Status == ClaimStatus.Denied
                    && row.claim.AdjudicatedAt >= row.flag.DecidedAt),
                DistinctClaimsDenied = grouped.Where(row => row.flag.Status == ClaimRiskFlagStatus.Confirmed
                        && row.flag.DecidedAt != null && row.claim.Status == ClaimStatus.Denied
                        && row.claim.AdjudicatedAt >= row.flag.DecidedAt)
                    .Select(row => row.claim.ClaimId).Distinct().Count(),
            }).ToListAsync(cancellationToken);
        var byRule = aggregates.ToDictionary(row => row.RuleCode);
        return ClaimRiskRuleCode.All.Select(rule =>
        {
            var row = byRule.GetValueOrDefault(rule);
            var decided = (row?.Cleared ?? 0) + (row?.Confirmed ?? 0);
            return new ClaimRiskFlagReportRow(rule, row?.Hits ?? 0, row?.Cleared ?? 0, row?.Confirmed ?? 0,
                decided == 0 ? 0 : decimal.Round(decimal.Divide(row!.Confirmed, decided), 4),
                row?.ConfirmedDenied ?? 0, row?.DistinctClaimsDenied ?? 0);
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> OpenRulesAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.ClaimRiskFlags.AsNoTracking().Where(row => row.TenantId == tenantId && row.ClaimId == claimId
                && row.Status == ClaimRiskFlagStatus.Open)
            .OrderBy(row => row.RuleCode).Select(row => row.RuleCode).ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ClaimRiskFlagRow>> RowsAsync(IReadOnlyList<FlagProjection> rows,
        CancellationToken cancellationToken)
    {
        var refs = rows.Select(row => row.Flag.DecidedBy).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var names = refs.Count == 0 ? new Dictionary<string, string>() : await users.DisplayNamesAsync(refs, cancellationToken);
        return rows.Select(row => new ClaimRiskFlagRow(row.Flag.FlagId, row.Flag.ClaimId, row.ClaimNumber,
            row.Flag.RuleCode, row.Flag.Status, Json(row.Flag.ThresholdSnapshot), Json(row.Flag.Evidence),
            row.Flag.RaisedAt, row.Flag.RaisedByEvaluation, row.Flag.DecidedAt,
            row.Flag.DecidedBy is { } actor ? names.GetValueOrDefault(actor) : null, row.Flag.DecisionReason)).ToList();
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value, LedgerJson.Options);

    private static void ValidateFilters(string? status, string? rule, DateOnly? from, DateOnly? to)
    {
        if (status is not (null or "" or "open" or "decided" or "all"))
            throw new CommandRefusedException("claim_risk_filters_invalid", "Risk flag status is 'open', 'decided' or 'all'.");
        if (!string.IsNullOrWhiteSpace(rule) && !ClaimRiskRuleCode.All.Contains(rule, StringComparer.Ordinal))
            throw new CommandRefusedException("claim_risk_filters_invalid", $"Unknown claim risk rule '{rule}'.");
        if (from > to) throw new CommandRefusedException("claim_risk_filters_invalid", "The from date cannot be after the to date.");
    }

    private static string Encode(DateTime raisedAt, Guid flagId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Cursor(raisedAt, flagId), LedgerJson.Options)));

    private static Cursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), LedgerJson.Options)
                ?? throw new CommandRefusedException("claim_risk_cursor_invalid", "The risk flag page cursor is invalid.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new CommandRefusedException("claim_risk_cursor_invalid", "The risk flag page cursor is invalid.");
        }
    }

    private sealed class FlagProjection
    {
        public required ClaimRiskFlag Flag { get; init; }
        public required string ClaimNumber { get; init; }
    }

    private sealed class ReportProjection
    {
        public required string RuleCode { get; init; }
        public int Hits { get; init; }
        public int Cleared { get; init; }
        public int Confirmed { get; init; }
        public int ConfirmedDenied { get; init; }
        public int DistinctClaimsDenied { get; init; }
    }

    private sealed class ReviewKeyProjection
    {
        public Guid ClaimId { get; init; }
        public DateTime FirstRaisedAt { get; init; }
    }

    private sealed class ClaimTotalProjection
    {
        public Guid ClaimId { get; init; }
        public decimal RequestedAmount { get; init; }
    }

    private sealed class OpenSummaryProjection
    {
        public int HeldClaims { get; init; }
        public int OpenFlags { get; init; }
        public DateTime OldestRaisedAt { get; init; }
    }

    private sealed class PeriodSummaryProjection
    {
        public int ClaimsFlagged { get; init; }
        public int FlagsRaised { get; init; }
        public int FlagsConfirmed { get; init; }
    }

    private sealed record Cursor(DateTime RaisedAt, Guid FlagId);
}
