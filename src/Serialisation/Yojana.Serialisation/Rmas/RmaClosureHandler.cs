using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Rmas;

public static class CloseRmaHandler
{
    public static async Task<CloseRmaResult> Handle(CloseRma command, SerialisationDbContext db,
        IRmaFollowUpQueryPort followUps, ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken)
    {
        var rma = await db.Rmas.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.RmaId == command.RmaId,
                cancellationToken)
            ?? throw new CommandRefusedException("rma_unknown", $"RMA {command.RmaId} does not exist.");
        if (rma.Stage == RmaStage.Closed)
            return new(rma.RmaId, rma.Stage, true, [], rma.LastEventId, true);
        if (rma.Stage != RmaStage.Dispositioned)
            throw new CommandRefusedException("rma_transition_not_allowed",
                $"RMA {rma.RmaId} is '{rma.Stage}'; it cannot close until it is dispositioned.",
                "receive and disposition every line first");

        var open = await followUps.OpenAsync(rma.RmaId, cancellationToken);
        if (open.Count > 0)
            return new(rma.RmaId, rma.Stage, false, open, null, false);

        var eventId = await RmaClosure.CloseAsync(rma, command.EventTime, command.ActorRef,
            null, clock.UtcNow, db, tenant, cancellationToken);
        return new(rma.RmaId, rma.Stage, true, [], eventId, false);
    }
}

public static class RmaDispositionFollowUpHandler
{
    public static Task Handle(RmaDispositioned message, SerialisationDbContext db, IRmaFollowUpQueryPort followUps,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken) =>
        RmaClosure.TryCloseAsync(message.RmaId, message.Header.EventTime,
            message.Header.ActorRef, message.Header.EventId, clock.UtcNow, db, followUps, tenant, cancellationToken);
}

public static class RmaReplacementFollowUpHandler
{
    public static Task Handle(ReplacementShipped message, SerialisationDbContext db, IRmaFollowUpQueryPort followUps,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken) =>
        message.SourceKind == ReplacementSourceKind.Rma
            ? RmaClosure.TryCloseAsync(message.SourceId, message.Header.EventTime,
                message.Header.ActorRef, message.Header.EventId, clock.UtcNow, db, followUps, tenant, cancellationToken)
            : Task.CompletedTask;
}

internal static class RmaClosure
{
    internal static async Task TryCloseAsync(Guid rmaId, DateTime eventTime, string actorRef,
        Guid? triggerEventId, DateTime recordedAt, SerialisationDbContext db, IRmaFollowUpQueryPort followUps,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var rma = await db.Rmas.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.RmaId == rmaId,
            cancellationToken);
        if (rma?.Stage != RmaStage.Dispositioned) return;
        if ((await followUps.OpenAsync(rmaId, cancellationToken)).Count != 0) return;
        await CloseAsync(rma, eventTime, actorRef, triggerEventId, recordedAt, db, tenant, cancellationToken);
    }

    internal static async Task<Guid> CloseAsync(Rma rma, DateTime eventTime, string actorRef,
        Guid? triggerEventId, DateTime recordedAt, SerialisationDbContext db, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        if (rma.DispositionedAt is { } dispositionedAt && eventTime < dispositionedAt)
            eventTime = dispositionedAt;
        if (!await db.RmaLines.AnyAsync(row => row.TenantId == tenant.TenantId && row.RmaId == rma.RmaId,
                cancellationToken))
            throw new CommandRefusedException("rma_lines_required", "An RMA without lines cannot be closed.");
        RmaStateMachine.Transition(rma, RmaStage.Closed);
        rma.ClosedAt = eventTime;
        rma.LastActorRef = actorRef;
        rma.LastModifiedAt = recordedAt;
        rma.LastModifiedBy = actorRef;
        // Closing is a document-stage fact. The trigger has already recorded every unit mutation in the ledger;
        // writing a projection-neutral unit event here would contend with unrelated unit commands.
        rma.LastEventId = triggerEventId ?? TimeOrderedId.New(eventTime);
        return rma.LastEventId;
    }
}
