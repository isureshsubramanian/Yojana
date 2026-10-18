using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Ledger;

// HIDDEN COUPLING (base-order ledger projection)
// WHAT:  this observer projects UnitEvent.attributes.base_order into ledger_base_order_ref before the append unit of
//        work commits. SelectionSetResolver reads that sparse table. BulkIngestion and LedgerBaseOrderBackfill perform
//        equivalent set-based inserts because their event rows deliberately bypass EF tracking.
// WHY:   the immutable JSON contract cannot be indexed without rewriting the 152 M-row partitioned ledger, while a
//        JSON fallback scan cannot serve an interactive document selection.
// WHAT BREAKS IF SEPARATED: a shipment can commit without being selectable by its originating order. Keep this
//        comment, SelectionSetResolver's reader comment and both set-based insert comments in step.
public sealed class LedgerBaseOrderReferenceObserver(SerialisationDbContext db) : IUnitEventAppendObserver
{
    public Task AppendedAsync(IReadOnlyList<UnitEvent> written, CancellationToken cancellationToken)
    {
        foreach (var @event in written)
        {
            // Direct bulk rows are detached and are projected by BulkIngestion's staging statement. Rows created by
            // UnitEventWriter are Added, so the reference commits with the same caller-owned SaveChanges transaction.
            if (db.Entry(@event).State != EntityState.Added
                || LedgerBaseOrderReferenceProjection.Read(@event.Attributes) is not { } document)
            {
                continue;
            }

            db.LedgerBaseOrderReferences.Add(new LedgerBaseOrderReference
            {
                TenantId = @event.TenantId,
                BaseDocumentType = document.Type,
                BaseDocumentId = document.Id,
                BaseDocumentLine = document.Line,
                UnitId = @event.UnitId,
                EventTime = @event.EventTime,
                EventId = @event.EventId,
            });
        }

        return Task.CompletedTask;
    }
}

/// <summary>The append-path parser for the recorded <c>base_order</c> contract.</summary>
internal static class LedgerBaseOrderReferenceProjection
{
    public static DocumentRef? Read(string? attributes)
    {
        if (attributes is null) return null;
        try
        {
            using var json = JsonDocument.Parse(attributes);
            if (!json.RootElement.TryGetProperty("base_order", out var value)
                || value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var document = value.Deserialize<DocumentRef>(LedgerJson.Options);
            return document is { Id: var id } && id != Guid.Empty && !string.IsNullOrWhiteSpace(document.Type)
                ? document
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
