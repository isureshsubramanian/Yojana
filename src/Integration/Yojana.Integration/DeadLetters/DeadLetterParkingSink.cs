using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.DeadLetters;

/// <summary>Persistence adapter used by relay and inbound recovery; it has no connector or retry policy.</summary>
public sealed class DeadLetterParkingSink(IntegrationDbContext db, ITenantContext tenant, IClock clock)
    : IOutboundCommandParkingSink, IInboundMessageParkingSink
{
    public async Task ParkAsync(OutboundCommandRow command, string reasonCode, string failure,
        CancellationToken cancellationToken)
    {
        var existing = db.DeadLetterItems.Local.FirstOrDefault(x => x.TenantId == tenant.TenantId
                && x.SourceKind == DeadLetterSourceKind.OutboundCommand && x.SourceId == command.OutboundCommandId)
            ?? await db.DeadLetterItems.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.SourceKind == DeadLetterSourceKind.OutboundCommand
                && x.SourceId == command.OutboundCommandId, cancellationToken);
        if (existing is not null) return;

        var id = TimeOrderedId.New(clock.UtcNow);
        db.DeadLetterItems.Add(new DeadLetterItem
        {
            TenantId = tenant.TenantId,
            DeadLetterItemId = id,
            ConnectionId = command.ConnectionId,
            Direction = DeadLetterDirection.Outbound,
            RecordType = command.CommandType,
            SourceKind = DeadLetterSourceKind.OutboundCommand,
            SourceId = command.OutboundCommandId,
            ReasonCode = reasonCode,
            RawPayload = command.Payload,
            CanonicalAttempt = command.Payload,
            Failure = failure,
            Status = DeadLetterStatus.NeedsAttention,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        });
        db.DeadLetterEvents.Add(Event(id, DeadLetterAction.Parked,
            $"system:connection:{command.ConnectionId:N}", failure, "{}"));

        if (reasonCode is OutboundConnectorOutcome.Unsupported or OutboundConnectorOutcome.Rejected
            || command.Outcome is OutboundConnectorOutcome.Unsupported or OutboundConnectorOutcome.Rejected)
        {
            db.ManualOutboundTasks.Add(new ManualOutboundTask
            {
                TenantId = tenant.TenantId,
                ManualOutboundTaskId = TimeOrderedId.New(clock.UtcNow),
                DeadLetterItemId = id,
                OutboundCommandId = command.OutboundCommandId,
                ConnectionId = command.ConnectionId,
                CommandType = command.CommandType,
                Draft = command.Payload,
                Status = ManualTaskStatus.Open,
                CreatedAt = clock.UtcNow,
            });
        }
    }

    public async Task<Guid> ParkInboundAsync(InboundMessage message, string reasonCode, string failure,
        CancellationToken cancellationToken)
    {
        var existing = db.DeadLetterItems.Local.FirstOrDefault(x => x.TenantId == tenant.TenantId
                && x.SourceKind == DeadLetterSourceKind.InboundMessage && x.SourceId == message.InboundMessageId)
            ?? await db.DeadLetterItems.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.SourceKind == DeadLetterSourceKind.InboundMessage
                && x.SourceId == message.InboundMessageId, cancellationToken);
        if (existing is not null) return existing.DeadLetterItemId;

        var id = TimeOrderedId.New(clock.UtcNow);
        db.DeadLetterItems.Add(new DeadLetterItem
        {
            TenantId = tenant.TenantId,
            DeadLetterItemId = id,
            ConnectionId = message.ConnectionId,
            Direction = DeadLetterDirection.Inbound,
            RecordType = message.MessageType,
            SourceKind = DeadLetterSourceKind.InboundMessage,
            SourceId = message.InboundMessageId,
            ReasonCode = reasonCode,
            RawPayload = message.RawPayload,
            CanonicalAttempt = message.CanonicalMessage,
            Failure = failure,
            Status = DeadLetterStatus.NeedsAttention,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        });
        db.DeadLetterEvents.Add(Event(id, DeadLetterAction.Parked,
            $"system:connection:{message.ConnectionId:N}", failure, "{}"));
        return id;
    }

    private DeadLetterEvent Event(Guid itemId, string action, string actor, string? reason, string details) => new()
    {
        TenantId = tenant.TenantId,
        DeadLetterEventId = TimeOrderedId.New(clock.UtcNow),
        DeadLetterItemId = itemId,
        Action = action,
        ActorRef = actor,
        Reason = reason,
        DetailsJson = details,
        OccurredAt = clock.UtcNow,
    };
}
