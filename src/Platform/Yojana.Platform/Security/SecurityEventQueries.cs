using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Security;

public sealed class SecurityEventQueries(
    PlatformDbContext db,
    ITenantContext tenant,
    IUserDirectory users,
    IClock clock)
{
    /// <summary>Oldest window start the summary accepts: seven days plus one tenant day boundary (ADR-0007).</summary>
    public static readonly TimeSpan SummaryLookBack = TimeSpan.FromDays(8);

    public async Task<SecurityEventPage> PageAsync(string? kind, Guid? userId, DateTime? from, DateTime? to,
        string? cursor, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take), "Take must be from 1 to 100.");
        if (kind is not null && !SecurityEventKind.All.Contains(kind))
            throw new ArgumentException("The security event kind is unknown.", nameof(kind));
        if (from is not null && to is not null && from > to)
            throw new ArgumentException("The from date must not be after the to date.", nameof(from));

        var query = TenantScope();
        if (kind is not null) query = query.Where(row => row.Kind == kind);
        if (userId is not null) query = query.Where(row => row.UserId == userId);
        if (from is not null) query = query.Where(row => row.OccurredAt >= from);
        if (to is not null) query = query.Where(row => row.OccurredAt < to);
        if (Decode(cursor) is { } after)
        {
            query = query.Where(row => row.OccurredAt < after.OccurredAt ||
                                       (row.OccurredAt == after.OccurredAt && row.Id.CompareTo(after.Id) < 0));
        }

        var rows = await query.OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.Id)
            .Take(take + 1)
            .Select(row => new SecurityEventRow(row.Id, row.OccurredAt, row.Kind, row.UserId, row.ActorUserId,
                row.ClientAddress, row.UserAgent, row.Reason))
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var references = rows.SelectMany(row => new[] { row.UserId, row.ActorUserId }).OfType<Guid>()
            .Distinct().Select(id => id.ToString("D")).ToList();
        var names = await users.DisplayNamesAsync(references, cancellationToken);
        string? Name(Guid? id) => id is null ? null : names.GetValueOrDefault(id.Value.ToString("D"));
        return new SecurityEventPage(rows.Select(row => new SecurityEventView(row.OccurredAt, row.Kind,
                Name(row.UserId), Name(row.ActorUserId), row.ClientAddress, row.UserAgent, row.Reason)).ToList(),
            hasMore ? Encode(rows[^1]) : null);
    }

    public async Task<SecurityEventSummary> SummaryAsync(DateTime? todayFrom, DateTime? weekFrom,
        CancellationToken cancellationToken)
    {
        if (todayFrom is null || weekFrom is null)
            throw new ArgumentException("Both todayFrom and weekFrom are required.", nameof(todayFrom));
        if (weekFrom > todayFrom)
            throw new ArgumentException("The week must not start after today.", nameof(weekFrom));
        if (weekFrom < clock.UtcNow - SummaryLookBack)
            throw new ArgumentException("The summary covers at most the last 8 days.", nameof(weekFrom));

        var today = todayFrom.Value;
        var counted = SecurityEventSummaryKinds.Today.Concat(SecurityEventSummaryKinds.Week).ToList();
        // One pass over the (tenant_id, occurred_at, id) range; today's kinds are split out by the CASE.
        var counts = await TenantScope()
            .Where(row => row.OccurredAt >= weekFrom && counted.Contains(row.Kind))
            .GroupBy(row => row.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Week = group.LongCount(),
                Today = group.LongCount(row => row.OccurredAt >= today),
            })
            .ToListAsync(cancellationToken);

        long Today(IReadOnlySet<string> kinds) => counts.Where(row => kinds.Contains(row.Kind)).Sum(row => row.Today);
        long Week(IReadOnlySet<string> kinds) => counts.Where(row => kinds.Contains(row.Kind)).Sum(row => row.Week);
        return new SecurityEventSummary(Today(SecurityEventSummaryKinds.SignIns),
            Today(SecurityEventSummaryKinds.FailedSignIns), Today(SecurityEventSummaryKinds.Lockouts),
            Week(SecurityEventSummaryKinds.PasswordResets), Week(SecurityEventSummaryKinds.MethodChanges));
    }

    /// <summary>
    /// Rows this tenant's administrator may see: the tenant's own rows plus tenant-less rows of its members (failed
    /// sign-ins before a tenant is chosen). The list and the summary both read through here so they cannot drift.
    /// </summary>
    private IQueryable<SecurityEvent> TenantScope()
    {
        var tenantId = tenant.TenantId;
        var memberIds = db.Memberships.Where(membership => membership.TenantId == tenantId)
            .Select(membership => membership.UserId);
        return db.SecurityEvents.AsNoTracking()
            .Where(row => row.TenantId == tenantId || (row.UserId != null && memberIds.Contains(row.UserId.Value)));
    }

    private static string Encode(SecurityEventRow row) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{row.OccurredAt.Ticks}|{row.Id:D}"));

    private static SecurityEventCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], out var ticks) && Guid.TryParse(parts[1], out var id))
                return new SecurityEventCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch (FormatException)
        {
            // Converted to the stable client error below.
        }
        throw new ArgumentException("The security event cursor is invalid.", nameof(cursor));
    }

    private sealed record SecurityEventCursor(DateTime OccurredAt, Guid Id);
    private sealed record SecurityEventRow(Guid Id, DateTime OccurredAt, string Kind, Guid? UserId,
        Guid? ActorUserId, string? ClientAddress, string? UserAgent, string Reason);
}
