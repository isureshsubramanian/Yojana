using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Documents;

/// <summary>Appends compensating facts for a cancelled foreign document; source facts remain immutable.</summary>
public static class DocumentCancellationPostedHandler
{
    public static Task<ConsumedEventResult> Handle(DocumentCancellationPosted message, DocumentContext ctx,
        CancellationToken cancellationToken) => ctx.InTransactionAsync<ConsumedEventResult>(async () =>
    {
        var tenantId = ctx.Tenant.TenantId;
        var originals = await ctx.Db.Events.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DocumentType == message.Document.Type
                && x.DocumentId == message.Document.Id && x.ReversesEventId == null
                && (x.EventType == UnitEventType.Shipped || x.EventType == UnitEventType.CustodyChanged
                    || x.EventType == UnitEventType.Delivered))
            .OrderByDescending(x => x.Seq).ToListAsync(cancellationToken);
        var selected = originals.GroupBy(x => x.UnitId).Select(x => x.First()).ToList();
        var drafts = new List<UnitEventDraft>();
        var exceptions = new List<string>();
        foreach (var original in selected)
        {
            var key = IdempotencyKey.For(message.Envelope.Source, message.Document.Type, message.Document.Id,
                original.DocumentLine, original.UnitId, UnitEventType.ShipmentReversed, message.Envelope.OccurredAt);
            if (await ctx.SeenAsync(key, message.Envelope.OccurredAt, cancellationToken)) continue;
            try
            {
                using var attributes = JsonDocument.Parse(original.Attributes ?? "{}");
                if (!attributes.RootElement.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object)
                {
                    exceptions.Add($"unit {original.UnitId:N}: cancellation source custody unavailable");
                    continue;
                }
                var target = new CustodyTarget(
                    from.GetProperty("location_kind").GetString() ?? LocationKind.Warehouse,
                    GuidValue(from, "location_id"), GuidValue(from, "party_ref"),
                    from.TryGetProperty("party_type", out var partyType) ? partyType.GetString() : null,
                    EpisodeReason.DocumentCorrection, "integration_cancellation");
                var move = await ctx.Episodes.PrepareCustodyMoveAsync(original.UnitId, target,
                    UnitEventType.ShipmentReversed, key, message.Envelope.OccurredAt, message.Envelope.Source,
                    message.Envelope.LegalEntityId, message.Document,
                    new Dictionary<string, object?> { ["cancelled_document"] = message.Document, ["message_id"] = message.Envelope.MessageId },
                    null, false, cancellationToken);
                drafts.Add(move.Draft with { ReversesEventId = original.EventId });

                var ownership = await ctx.Episodes.OpenAsync(original.UnitId, EpisodeKind.Ownership, cancellationToken);
                if (ownership?.OpeningDocumentType == message.Document.Type && ownership.OpeningDocumentId == message.Document.Id)
                {
                    var closed = await ctx.Episodes.PrepareOwnershipCloseAsync(original.UnitId,
                        EpisodeReason.DocumentCorrection, IdempotencyKey.Derive(key, "ownership-close"),
                        message.Envelope.OccurredAt, message.Envelope.Source, message.Envelope.LegalEntityId,
                        message.Document, cancellationToken);
                    drafts.Add(closed.Draft with { ReversesEventId = ownership.OpeningEventId });
                }
            }
            catch (CommandRefusedException ex)
            {
                exceptions.Add($"unit {original.UnitId:N}: {ex.Rule}: {ex.Message}");
            }
        }
        var append = await ctx.Writer.AppendAsync(drafts, cancellationToken);
        return new("Document.Cancelled", message.Document, append.Written.Count, 0, exceptions,
            append.Written.Count == 0 && exceptions.Count == 0);
    }, cancellationToken);

    private static Guid? GuidValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var parsed) ? parsed : null;
}
