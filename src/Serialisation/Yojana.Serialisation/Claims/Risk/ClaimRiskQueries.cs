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
        if (status is not (null or "" or "open" or "decided"))
            throw new CommandRefusedException("claim_risk_filters_invalid", "Risk flag status is 'open' or 'decided'.");
        if (!string.IsNullOrWhiteSpace(rule) && !ClaimRiskRuleCode.All.Contains(rule, StringComparer.Ordinal))
            throw new CommandRefusedException("claim_risk_filters_invalid", $"Unknown claim risk rule '{rule}'.");
        if (from > to) throw new CommandRefusedException("claim_risk_filters_invalid", "The from date cannot be after the to date.");
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
            }).ToListAsync(cancellationToken);
        var byRule = aggregates.ToDictionary(row => row.RuleCode);
        return ClaimRiskRuleCode.All.Select(rule =>
        {
            var row = byRule.GetValueOrDefault(rule);
            var decided = (row?.Cleared ?? 0) + (row?.Confirmed ?? 0);
            return new ClaimRiskFlagReportRow(rule, row?.Hits ?? 0, row?.Cleared ?? 0, row?.Confirmed ?? 0,
                decided == 0 ? 0 : decimal.Round(decimal.Divide(row!.Confirmed, decided), 4), row?.ConfirmedDenied ?? 0);
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
    }

    private sealed record Cursor(DateTime RaisedAt, Guid FlagId);
}
