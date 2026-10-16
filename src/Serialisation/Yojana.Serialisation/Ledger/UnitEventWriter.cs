using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Projections;

namespace Yojana.Serialisation.Ledger;

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 2)
// WHAT:  UnitEventWriter.Append(...) invokes UnitCurrentStateProjector.Apply(...) in the same
//        transaction and the same ordering (events for one unit are applied in seq order); the
//        projector reads unit_current.last_event_seq and refuses an out-of-order apply.
// WHY:   unit_current is the row every operational query (ATP, picking, entitlement check, RMA receipt)
//        reads; an asynchronous projection would let a shipment be picked twice or a returned unit be
//        allocated before the return is visible. The ledger is the truth; the projection must be
//        read-your-writes consistent with it.
// WHAT BREAKS IF SEPARATED: stale current state under load; double allocation of a serial; the
//        replay-and-diff healer would become the primary path instead of a safety net. The async
//        projections (dashboards, passport exports) are deliberately NOT part of this coupling and
//        may lag. Change the seq contract and this comment together, on both sides.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a distributed one.

/// <summary>
/// Appends events and projects them inline (see the coupling comment above and in <see cref="UnitCurrentStateProjector"/>).
/// Sequence numbers come from <c>seq_unit_event</c> through <c>sp_sequence_get_range</c>, one range per append, on the
/// context's own connection so they are taken inside the caller's transaction when one is open.
/// </summary>
public sealed class UnitEventWriter(SerialisationDbContext db, ITenantContext tenant, IClock clock, UnitCurrentStateProjector projector, PublishedEventMapper publisher) : IUnitEventWriter
{
    public async Task<AppendResult> AppendAsync(IReadOnlyList<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
        {
            return new AppendResult([], []);
        }

        var tenantId = tenant.TenantId;
        var recordedTime = clock.UtcNow;

        // Idempotency: a draft whose (key, business time) is already in the ledger is a no-op, reported, never re-applied.
        var keys = drafts.Select(d => d.IdempotencyKey).Distinct().ToList();
        var existing = await db.Events.AsNoTracking()
            .Where(e => keys.Contains(e.IdempotencyKey))
            .Select(e => new { e.IdempotencyKey, e.EventTime })
            .ToListAsync(cancellationToken);
        var existingSet = existing.Select(e => (e.IdempotencyKey, e.EventTime)).ToHashSet();
        var seenInBatch = new HashSet<(string, DateTime)>();
        var duplicates = new List<UnitEventDraft>();
        var fresh = new List<UnitEventDraft>();
        foreach (var draft in drafts)
        {
            var key = (draft.IdempotencyKey, draft.EventTime);
            if (existingSet.Contains(key) || !seenInBatch.Add(key))
            {
                duplicates.Add(draft);
            }
            else
            {
                fresh.Add(draft);
            }
        }

        if (fresh.Count == 0)
        {
            return new AppendResult([], duplicates);
        }

        // Seq order within the append: per unit by business time, then by the caller's order (recorded time is the
        // same instant for the whole append), so the history ordering rule and seq never disagree for one append.
        var ordered = fresh.Select((d, i) => (Draft: d, Index: i)).OrderBy(x => x.Draft.UnitId).ThenBy(x => x.Draft.EventTime).ThenBy(x => x.Index).ToList();
        var firstSeq = await NextRangeAsync(ordered.Count, cancellationToken);
        var written = new List<UnitEvent>(ordered.Count);
        var seq = firstSeq;
        foreach (var (draft, _) in ordered)
        {
            var @event = new UnitEvent
            {
                TenantId = tenantId,
                EventTime = draft.EventTime,
                EventId = draft.PresetEventId ?? TimeOrderedId.New(draft.EventTime),
                UnitId = draft.UnitId,
                Seq = seq++,
                EventType = draft.EventType,
                BizStep = draft.BizStep,
                Disposition = draft.Disposition,
                LocationId = draft.LocationId,
                LegalEntityId = draft.LegalEntityId,
                ActorRef = draft.ActorRef,
                DocumentType = draft.DocumentType,
                DocumentId = draft.DocumentId,
                DocumentLine = draft.DocumentLine,
                RecordedTime = recordedTime,
                PostingPeriod = draft.PostingPeriod,
                IdempotencyKey = draft.IdempotencyKey,
                ReversesEventId = draft.ReversesEventId,
                Attributes = AttributesJson(draft),
            };
            db.Events.Add(@event);
            written.Add(@event);
        }

        // Inline projection, in seq order per unit, within this same unit of work (coupling 2).
        foreach (var group in written.GroupBy(e => e.UnitId))
        {
            var deltas = group.OrderBy(e => e.Seq).Select(e => (e, fresh.First(d => d.IdempotencyKey == e.IdempotencyKey && d.EventTime == e.EventTime).Delta)).ToList();
            await projector.ApplyAsync(tenantId, group.Key, deltas, cancellationToken);
        }

        // Published events (P1-T16) leave through the outbox when this unit of work commits.
        await publisher.PublishAsync(written, cancellationToken);
        return new AppendResult(written, duplicates);
    }

    /// <summary>Event attributes: the caller's JSON object plus the projection delta under <c>_state</c>, so a replay needs no event semantics.</summary>
    internal static string? AttributesJson(UnitEventDraft draft)
    {
        if (draft.Attributes is null && draft.Delta is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (draft.Attributes is not null)
            {
                using var doc = JsonDocument.Parse(draft.Attributes.Json);
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name != UnitCurrentStateProjector.StateKey)
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            if (draft.Delta is not null)
            {
                writer.WritePropertyName(UnitCurrentStateProjector.StateKey);
                JsonSerializer.Serialize(writer, draft.Delta, LedgerJson.Options);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<long> NextRangeAsync(int size, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "EXEC sys.sp_sequence_get_range @sequence_name = @name, @range_size = @size, @range_first_value = @first OUTPUT;";
        var name = command.CreateParameter(); name.ParameterName = "@name"; name.DbType = DbType.String; name.Value = $"[{SerialisationDbContext.SchemaName}].[{SerialisationDbContext.EventSequence}]"; command.Parameters.Add(name);
        var count = command.CreateParameter(); count.ParameterName = "@size"; count.DbType = DbType.Int64; count.Value = (long)size; command.Parameters.Add(count);
        var first = command.CreateParameter(); first.ParameterName = "@first"; first.DbType = DbType.Object; first.Direction = ParameterDirection.Output; command.Parameters.Add(first);
        await command.ExecuteNonQueryAsync(cancellationToken);
        // The connection stays open for the rest of the unit of work; EF closes it with the context (keeps the
        // session context the RLS predicate reads, which was set when the connection opened).
        return Convert.ToInt64(first.Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
