using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Platform.Contracts.Identifiers;

namespace Yojana.Serialisation.Documents;

/// <summary>Appends compensating facts for a cancelled foreign document; source facts remain immutable.</summary>
public static class DocumentCancellationPostedHandler
{
    public static Task<ConsumedEventResult> Handle(DocumentCancellationPosted message, DocumentContext ctx,
        CancellationToken cancellationToken) => ctx.InTransactionAsync<ConsumedEventResult>(async () =>
    {
        var tenantId = ctx.Tenant.TenantId;
        var drafts = new List<UnitEventDraft>();
        var exceptions = new List<string>();

        var commissioned = await ctx.Db.Events.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DocumentType == message.Document.Type
                && x.DocumentId == message.Document.Id && x.EventType == UnitEventType.Commissioned)
            .OrderBy(x => x.Seq).ToListAsync(cancellationToken);
        var receiptCommissions = commissioned.Where(IsReceiptOrigin).ToList();
        if (receiptCommissions.Count > 0)
        {
            var commissionEventIds = receiptCommissions.Select(x => x.EventId).ToList();
            var receiptCreated = await ctx.Db.Episodes.AsNoTracking()
                .Where(x => x.TenantId == tenantId && commissionEventIds.Contains(x.OpeningEventId)
                    && x.Source == EpisodeSource.Receipt)
                .Select(x => x.OpeningEventId).Distinct().ToListAsync(cancellationToken);
            var receiptCreatedSet = receiptCreated.ToHashSet();
            receiptCommissions = receiptCommissions.Where(x => receiptCreatedSet.Contains(x.EventId)).ToList();
        }

        if (receiptCommissions.Count > 0)
        {
            var unitIds = receiptCommissions.Select(x => x.UnitId).Distinct().ToList();
            var allowedOpenings = await ctx.Db.Episodes.AsNoTracking()
                .Where(x => x.TenantId == tenantId && unitIds.Contains(x.UnitId)
                    && x.OpeningDocumentType == message.Document.Type && x.OpeningDocumentId == message.Document.Id
                    && x.Source == EpisodeSource.Receipt)
                .Select(x => x.OpeningEventId).ToListAsync(cancellationToken);
            var allowedOpeningSet = allowedOpenings.ToHashSet();
            var facts = await ctx.Db.Events.AsNoTracking().Where(x => x.TenantId == tenantId && unitIds.Contains(x.UnitId))
                .Select(x => new { x.UnitId, x.EventId, x.Seq, x.EventType, x.EventTime }).ToListAsync(cancellationToken);
            var factsByUnit = facts.GroupBy(x => x.UnitId).ToDictionary(x => x.Key, x => x.ToList());
            var units = await ctx.Db.Units.Where(x => x.TenantId == tenantId && unitIds.Contains(x.UnitId))
                .ToDictionaryAsync(x => x.UnitId, cancellationToken);
            var keys = receiptCommissions.ToDictionary(x => x.EventId, x => IdempotencyKey.For(
                message.Envelope.Source, message.Document.Type, message.Document.Id, x.DocumentLine, x.UnitId,
                UnitEventType.ReceiptReversed, message.Envelope.OccurredAt));
            var seen = await ctx.SeenManyAsync(keys.Values.Select(key => (key, message.Envelope.OccurredAt)).ToList(),
                cancellationToken);

            foreach (var original in receiptCommissions)
            {
                var key = keys[original.EventId];
                if (seen.Contains((key, message.Envelope.OccurredAt))) continue;
                var later = factsByUnit[original.UnitId]
                    .Where(x => x.Seq > original.Seq && !allowedOpeningSet.Contains(x.EventId))
                    .OrderBy(x => x.Seq).ToList();
                if (later.Count > 0)
                {
                    exceptions.Add($"unit {original.UnitId:N}: receipt reversal refused; later facts: "
                        + string.Join(", ", later.Select(x => $"{x.EventType} at {x.EventTime:O}")));
                    continue;
                }
                if (!units.TryGetValue(original.UnitId, out var unit) || unit.LifecycleStatus != LifecycleStatus.Released)
                {
                    exceptions.Add($"unit {original.UnitId:N}: receipt reversal refused; lifecycle is "
                        + (unit?.LifecycleStatus ?? "unavailable"));
                    continue;
                }

                var next = UnitLifecycle.Require(unit.LifecycleStatus, UnitEventType.ReceiptReversed, unit.Origin,
                    unit.UnitId);
                var eventId = TimeOrderedId.New(message.Envelope.OccurredAt);
                var closedEpisodes = await ctx.Episodes.CloseReceiptEpisodesAsync(unit.UnitId,
                    new(message.Document.Type, message.Document.Id, original.DocumentLine), message.Envelope.OccurredAt,
                    eventId, cancellationToken);
                var identifiers = await ctx.Db.Identifiers.Where(x => x.TenantId == tenantId
                    && x.UnitId == unit.UnitId && x.ValidTo == null).ToListAsync(cancellationToken);
                foreach (var identifier in identifiers)
                {
                    identifier.ValidTo = message.Envelope.OccurredAt;
                    identifier.CorrectionReason = EpisodeReason.DocumentCorrection;
                }

                var closedCustody = closedEpisodes.Any(x => x.Kind == EpisodeKind.Custody);
                var closedOwnership = closedEpisodes.Any(x => x.Kind == EpisodeKind.Ownership);
                drafts.Add(new UnitEventDraft
                {
                    UnitId = unit.UnitId,
                    EventTime = message.Envelope.OccurredAt,
                    EventType = UnitEventType.ReceiptReversed,
                    PresetEventId = eventId,
                    BizStep = "decommissioning",
                    Disposition = UnitLifecycle.DispositionFor(next),
                    LegalEntityId = message.Envelope.LegalEntityId,
                    ActorRef = message.Envelope.Source,
                    DocumentType = message.Document.Type,
                    DocumentId = message.Document.Id,
                    DocumentLine = original.DocumentLine,
                    IdempotencyKey = key,
                    ReversesEventId = original.EventId,
                    Attributes = JsonObjectText.Of(new
                    {
                        reason = EpisodeReason.DocumentCorrection,
                        commissioned_event_id = original.EventId,
                        closed_identifier_ids = identifiers.Select(x => x.IdentifierId).ToList(),
                        closed_episode_ids = closedEpisodes.Select(x => x.EpisodeId).ToList(),
                    }),
                    Delta = new CurrentStateDelta
                    {
                        LifecycleStatus = next,
                        Disposition = UnitLifecycle.DispositionFor(next),
                        LocationId = closedCustody ? new Optional<Guid?>(null) : null,
                        LocationKind = closedCustody ? new Optional<string?>(null) : null,
                        CustodyPartyRef = closedCustody ? new Optional<Guid?>(null) : null,
                        CustodyKind = closedCustody ? new Optional<string?>(null) : null,
                        OpenCustodyEpisodeId = closedCustody ? new Optional<Guid?>(null) : null,
                        OwnerPartyRef = closedOwnership ? new Optional<Guid?>(null) : null,
                        OpenOwnershipEpisodeId = closedOwnership ? new Optional<Guid?>(null) : null,
                    },
                });
                unit.LifecycleStatus = next;
            }
        }

        var originals = await ctx.Db.Events.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DocumentType == message.Document.Type
                && x.DocumentId == message.Document.Id && x.ReversesEventId == null
                && (x.EventType == UnitEventType.Shipped || x.EventType == UnitEventType.CustodyChanged
                    || x.EventType == UnitEventType.Delivered))
            .OrderByDescending(x => x.Seq).ToListAsync(cancellationToken);
        var selected = originals.GroupBy(x => x.UnitId).Select(x => x.First()).ToList();
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

    private static bool IsReceiptOrigin(UnitEvent original)
    {
        using var attributes = JsonDocument.Parse(original.Attributes ?? "{}");
        return attributes.RootElement.TryGetProperty("origin", out var origin)
            && origin.GetString() is UnitOrigin.Purchased or UnitOrigin.VendorAsn;
    }
}
