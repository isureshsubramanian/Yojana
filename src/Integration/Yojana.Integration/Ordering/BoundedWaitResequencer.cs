using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Ordering;

public sealed record ResequenceDecision(bool Held, DateTime? HoldUntil);

public sealed class BoundedWaitResequencer(IntegrationDbContext db, ITenantContext tenant, IClock clock, IMessageBus bus)
{
    public const int DefaultWaitMinutes = 15;

    public async Task<ResequenceDecision> HoldAsync(Guid inboundMessageId, Guid connectionId,
        IReadOnlyCollection<string> dependencyKeys, DateTime eventTime, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.ResequenceItems.AnyAsync(x => x.TenantId == tenantId && x.InboundMessageId == inboundMessageId,
                cancellationToken))
            return new(true, await db.ResequenceItems.Where(x => x.TenantId == tenantId && x.InboundMessageId == inboundMessageId)
                .Select(x => (DateTime?)x.HoldUntil).SingleAsync(cancellationToken));
        var minutes = await db.ConnectionGatewaySettings.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .Select(x => (int?)x.OrderingWaitMinutes).SingleOrDefaultAsync(cancellationToken) ?? DefaultWaitMinutes;
        var heldAt = clock.UtcNow;
        var holdUntil = heldAt.AddMinutes(minutes);
        // One row per message: its dependency key is the sorted set, so release checks stay deterministic and no
        // hidden ordering exists between several serials on one document.
        db.ResequenceItems.Add(new ResequenceItem
        {
            TenantId = tenantId, ResequenceItemId = TimeOrderedId.New(heldAt), ConnectionId = connectionId,
            InboundMessageId = inboundMessageId,
            DependencyKey = string.Join('|', dependencyKeys.Order(StringComparer.OrdinalIgnoreCase)),
            EventTime = eventTime, HeldAt = heldAt, HoldUntil = holdUntil, Status = ResequenceStatus.Held,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(true, holdUntil);
    }

    public async Task<int> ReleaseSatisfiedAsync(Guid connectionId, IReadOnlyCollection<string> availableKeys,
        CancellationToken cancellationToken)
    {
        var set = availableKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var held = await db.ResequenceItems.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.Status == ResequenceStatus.Held).OrderBy(x => x.EventTime).ThenBy(x => x.ResequenceItemId)
            .ToListAsync(cancellationToken);
        var candidates = held.Where(x => x.DependencyKey.Split('|').Any(set.Contains)).ToList();
        var release = new List<ResequenceItem>();
        foreach (var row in candidates)
        {
            var keys = row.DependencyKey.Split('|');
            var known = await bus.InvokeAsync<Yojana.Serialisation.Contracts.ResolveKnownSerialsResult>(
                new Yojana.Serialisation.Contracts.ResolveKnownSerials(keys),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);
            if (known.Unknown.Count == 0) release.Add(row);
        }
        return await ReleaseAsync(release, late: false, cancellationToken);
    }

    public async Task<int> ReleaseExpiredAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var expired = await db.ResequenceItems.Where(x => x.TenantId == tenant.TenantId
            && x.Status == ResequenceStatus.Held && x.HoldUntil <= now)
            .OrderBy(x => x.EventTime).ThenBy(x => x.ResequenceItemId).ToListAsync(cancellationToken);
        return await ReleaseAsync(expired, late: true, cancellationToken);
    }

    private async Task<int> ReleaseAsync(List<ResequenceItem> rows, bool late,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var row in rows)
        {
            row.Status = ResequenceStatus.Released;
            row.ReleasedAt = now;
            row.Late = late;
            var message = await db.InboundMessages.SingleAsync(x => x.TenantId == tenant.TenantId
                && x.InboundMessageId == row.InboundMessageId, cancellationToken);
            message.TranslationStatus = late ? InboundTranslationStatus.Late : InboundTranslationStatus.Pending;
            message.NextTranslationAttemptAt = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var row in rows)
            await bus.PublishAsync(new TranslateInboundMessage(row.InboundMessageId),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return rows.Count;
    }
}
