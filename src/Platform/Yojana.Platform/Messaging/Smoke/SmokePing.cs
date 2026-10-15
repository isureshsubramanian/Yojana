using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Messaging.Smoke;

/// <summary>P0 smoke message: published inside a request transaction, delivered after commit through the outbox.</summary>
public sealed record SmokePing(string Note);

/// <summary>Records that the smoke message arrived, under the tenant carried by the envelope.</summary>
public static class SmokePingHandler
{
    public static async Task Handle(SmokePing message, PlatformDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        db.SmokeLogs.Add(new SmokeLog
        {
            TenantId = tenant.TenantId,
            Id = TimeOrderedId.New(clock.UtcNow),
            Source = "handler",
            Note = message.Note,
            RecordedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public static class SmokeQueries
{
    public static Task<List<SmokeLog>> RecentAsync(PlatformDbContext db, int take, CancellationToken cancellationToken) =>
        db.SmokeLogs.OrderByDescending(s => s.RecordedAt).Take(take).ToListAsync(cancellationToken);
}
