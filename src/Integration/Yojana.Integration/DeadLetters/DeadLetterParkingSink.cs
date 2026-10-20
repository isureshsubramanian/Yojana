using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.DeadLetters;

/// <summary>Persistence adapter used by relay and inbound recovery; it has no connector or retry policy.</summary>
public sealed class DeadLetterParkingSink(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ManualTaskDeskSettings deskSettings)
    : IOutboundCommandParkingSink, IInboundMessageParkingSink, IOutboundReviewTaskSink
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
            // One task per command (unique index): a review that ends rejected keeps its task, now tied to the letter.
            if (await OpenTaskAsync(command, cancellationToken) is { } reviewTask)
            {
                reviewTask.DeadLetterItemId = id;
                reviewTask.Note = Cap(failure);
                return;
            }
            var desk = await deskSettings.ResolveAsync(command.CommandType, cancellationToken);
            db.ManualOutboundTasks.Add(new ManualOutboundTask
            {
                TenantId = tenant.TenantId,
                ManualOutboundTaskId = TimeOrderedId.New(clock.UtcNow),
                DeadLetterItemId = id,
                OutboundCommandId = command.OutboundCommandId,
                ConnectionId = command.ConnectionId,
                CommandType = command.CommandType,
                Desk = desk,
                Draft = command.Payload,
                Note = Cap(failure),
                Status = ManualTaskStatus.Open,
                CreatedAt = clock.UtcNow,
            });
        }
    }

    public async Task OpenAsync(OutboundCommandRow command, string note, CancellationToken cancellationToken)
    {
        if (await OpenTaskAsync(command, cancellationToken) is { } existing)
        {
            existing.Note = Cap(note);
            return;
        }
        db.ManualOutboundTasks.Add(new ManualOutboundTask
        {
            TenantId = tenant.TenantId,
            ManualOutboundTaskId = TimeOrderedId.New(clock.UtcNow),
            OutboundCommandId = command.OutboundCommandId,
            ConnectionId = command.ConnectionId,
            CommandType = command.CommandType,
            Desk = await deskSettings.ResolveAsync(command.CommandType, cancellationToken),
            Draft = command.Payload,
            Note = Cap(note),
            Status = ManualTaskStatus.Open,
            CreatedAt = clock.UtcNow,
        });
    }

    public async Task CloseAsync(OutboundCommandRow command, OutboundDocument document,
        CancellationToken cancellationToken)
    {
        if (await OpenTaskAsync(command, cancellationToken) is not { } task) return;
        task.Status = ManualTaskStatus.Completed;
        task.ErpExternalId = document.ErpExternalId;
        task.ErpChangeMarker = document.ErpChangeMarker;
        task.CompletedBy = $"system:connection:{command.ConnectionId:N}";
        task.CompletedAt = clock.UtcNow;
    }

    private async Task<ManualOutboundTask?> OpenTaskAsync(OutboundCommandRow command,
        CancellationToken cancellationToken) =>
        db.ManualOutboundTasks.Local.FirstOrDefault(x => x.TenantId == tenant.TenantId
            && x.OutboundCommandId == command.OutboundCommandId && x.Status == ManualTaskStatus.Open)
        ?? await db.ManualOutboundTasks.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.OutboundCommandId == command.OutboundCommandId && x.Status == ManualTaskStatus.Open,
            cancellationToken);

    private static string Cap(string value) => value.Length <= 1000 ? value : value[..1000];

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
