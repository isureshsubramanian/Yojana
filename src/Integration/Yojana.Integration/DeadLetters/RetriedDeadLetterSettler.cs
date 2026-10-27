using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.DeadLetters;

/// <summary>
/// Closes the loop on a dead letter an operator retried: resolved when its source runs to completion, back to
/// needs-attention when the source stops for a person again. Without it a retried item stays retry-pending forever.
/// </summary>
public sealed class RetriedDeadLetterSettler(IntegrationDbContext db, ITenantContext tenant, IClock clock)
{
    /// <summary>Settles after a translation attempt. Reads the stored state: the gateway may finish by bulk update.</summary>
    public async Task SettleInboundAsync(Guid inboundMessageId, CancellationToken cancellationToken)
    {
        var source = await db.InboundMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.InboundMessageId == inboundMessageId)
            .Select(x => new { x.TranslationStatus, x.AttentionReasonCode, x.LastTranslationError, x.ConnectionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null) return;
        var item = await RetriedAsync(DeadLetterSourceKind.InboundMessage, inboundMessageId, cancellationToken);
        if (item is null) return;
        if (source.TranslationStatus == InboundTranslationStatus.Completed)
            Resolve(item);
        else if (source.TranslationStatus == InboundTranslationStatus.NeedsAttention)
        {
            item.Status = DeadLetterStatus.NeedsAttention;
            item.ReasonCode = source.AttentionReasonCode ?? item.ReasonCode;
            item.Failure = source.LastTranslationError ?? item.Failure;
            item.ResolvedAt = null;
            item.UpdatedAt = clock.UtcNow;
            AddEvent(item.DeadLetterItemId, DeadLetterAction.Parked, $"system:connection:{source.ConnectionId:N}",
                item.Failure);
        }
        else return;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Marks a retried outbound item resolved; the caller saves it in the command's completion commit.</summary>
    public async Task ResolveOutboundAsync(Guid outboundCommandId, CancellationToken cancellationToken)
    {
        if (await RetriedAsync(DeadLetterSourceKind.OutboundCommand, outboundCommandId, cancellationToken) is { } item)
            Resolve(item);
    }

    private Task<DeadLetterItem?> RetriedAsync(string sourceKind, Guid sourceId, CancellationToken cancellationToken) =>
        db.DeadLetterItems.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.SourceKind == sourceKind
            && x.SourceId == sourceId && x.Status == DeadLetterStatus.RetryPending, cancellationToken);

    private void Resolve(DeadLetterItem item)
    {
        item.Status = DeadLetterStatus.Resolved;
        item.ResolvedAt = clock.UtcNow;
        item.UpdatedAt = clock.UtcNow;
        AddEvent(item.DeadLetterItemId, DeadLetterAction.RetrySucceeded, $"system:connection:{item.ConnectionId:N}", null);
    }

    private void AddEvent(Guid itemId, string action, string actor, string? reason) => db.DeadLetterEvents.Add(new()
    {
        TenantId = tenant.TenantId,
        DeadLetterEventId = TimeOrderedId.New(clock.UtcNow),
        DeadLetterItemId = itemId,
        Action = action,
        ActorRef = actor,
        Reason = reason is { Length: > 2000 } ? reason[..1900] + " (full field list in event details)" : reason,
        DetailsJson = "{}",
        OccurredAt = clock.UtcNow,
    });
}
