using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public sealed class SecurityEventQueries(
    PlatformDbContext db,
    ITenantContext tenant,
    IUserDirectory users)
{
    public async Task<SecurityEventPage> PageAsync(string? kind, Guid? userId, DateTime? from, DateTime? to,
        string? cursor, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take), "Take must be from 1 to 100.");
        if (kind is not null && !SecurityEventKind.All.Contains(kind))
            throw new ArgumentException("The security event kind is unknown.", nameof(kind));
        if (from is not null && to is not null && from > to)
            throw new ArgumentException("The from date must not be after the to date.", nameof(from));

        var tenantId = tenant.TenantId;
        var memberIds = db.Memberships.Where(membership => membership.TenantId == tenantId)
            .Select(membership => membership.UserId);
        var query = db.SecurityEvents.AsNoTracking()
            .Where(row => row.TenantId == tenantId || (row.UserId != null && memberIds.Contains(row.UserId.Value)));
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
