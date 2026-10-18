using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Selection;

/// <summary>
/// Turns a <see cref="SelectionSource"/> into a stored selection set (ADR-0001 "Bulk selection sets"). A document source
/// is every unit the document's events touched, including a recorded base document such as a sales order; a customer is
/// every unit the party currently owns; an item is every unit whose current assignment is that item; a date range is
/// every unit with an event in it; a pasted list resolves serial by serial and reports the unknown ones; a serial range
/// expands within the scheme's block (a boundary-crossing range is rejected, never partially expanded) and reports
/// serials that are not units yet.
/// </summary>
public sealed class SelectionSetResolver(SerialisationDbContext db, UnitResolver resolver, ISerialSchemeSource schemes,
    IImportJobSource imports, ITenantContext tenant, IClock clock)
{
    public const int MaxUnits = 100_000;

    private sealed class LegacyBaseOrderUnit
    {
        public Guid UnitId { get; set; }
    }

    public async Task<ResolveSelectionSetResult> ResolveAsync(ResolveSelectionSet command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var existing = await db.SelectionSets.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SelectionId == command.CommandId, cancellationToken);
        if (existing is not null)
        {
            return new ResolveSelectionSetResult(existing.SelectionId, existing.ResolvedCount, [], [], JsonSerializer.Deserialize<SelectionSource>(existing.SourceJson, LedgerJson.Options)!);
        }

        var source = command.Source;
        var unknown = new List<string>();
        var rejected = new List<string>();
        var units = new Dictionary<Guid, string?>();
        switch (source.Kind)
        {
            case SelectionSource.Kinds.Document:
            {
                var doc = source.Document ?? throw new CommandRefusedException("source_invalid", "A document source needs a document reference.");
                var ids = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == doc.Type && e.DocumentId == doc.Id && (doc.Line == null || e.DocumentLine == doc.Line))
                    .Select(e => e.UnitId).Distinct().OrderBy(id => id).Take(MaxUnits + 1).ToListAsync(cancellationToken);
                foreach (var id in ids) units[id] = null;
                // HIDDEN COUPLING (base-order ledger projection): shipment facts retain their originating order in
                // immutable JSON, while the append boundary writes the sparse lookup queried here in the same
                // transaction. Reading the JSON ledger directly would turn this interactive selection into a scan;
                // changing the base_order attribute or append projection requires changing this reader with it.
                var linkedIds = await db.LedgerBaseOrderReferences.AsNoTracking()
                    .Where(reference => reference.TenantId == tenantId
                        && reference.BaseDocumentType == doc.Type && reference.BaseDocumentId == doc.Id
                        && (doc.Line == null || reference.BaseDocumentLine == doc.Line))
                    .Select(reference => reference.UnitId).Distinct().OrderBy(id => id).Take(MaxUnits + 1)
                    .ToListAsync(cancellationToken);
                foreach (var id in linkedIds) units[id] = null;

                var checkpoint = await db.LedgerBaseOrderBackfillCheckpoints.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
                if (checkpoint?.Status != LedgerBaseOrderBackfillStatus.Complete)
                {
                    // HIDDEN COUPLING (temporary historical fallback): the backfill completes newer months first, so
                    // its checkpoint month is the exclusive upper bound of history that may still lack a sparse row.
                    // Removing this fallback before the checkpoint reaches complete makes pre-upgrade orders disappear;
                    // removing the bound restores the full-ledger scan that the sparse projection exists to retire.
                    var before = checkpoint?.CheckpointMonth?.AddMonths(1);
                    var legacyIds = await ResolveLegacyBaseOrdersAsync(tenantId, doc, before, cancellationToken);
                    foreach (var id in legacyIds) units[id] = null;
                }
                break;
            }

            case SelectionSource.Kinds.Customer:
            {
                var party = source.PartyRef ?? throw new CommandRefusedException("source_invalid", "A customer source needs a party reference.");
                var ids = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.Kind == EpisodeKind.Ownership && e.PartyRef == party && e.ValidTo == null).Select(e => e.UnitId).Take(MaxUnits + 1).ToListAsync(cancellationToken);
                foreach (var id in ids) units[id] = null;
                break;
            }

            case SelectionSource.Kinds.Item:
            {
                var item = source.ItemId ?? throw new CommandRefusedException("source_invalid", "An item source needs an item id.");
                var ids = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.ItemId == item && a.ValidTo == null).Select(a => a.UnitId).Take(MaxUnits + 1).ToListAsync(cancellationToken);
                foreach (var id in ids) units[id] = null;
                break;
            }

            case SelectionSource.Kinds.DateRange:
            {
                if (source.From is null || source.To is null || source.To < source.From) throw new CommandRefusedException("source_invalid", "A date range needs From and To with From <= To.");
                var query = db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.EventTime >= source.From && e.EventTime < source.To);
                if (source.EventType is not null) query = query.Where(e => e.EventType == source.EventType);
                var ids = await query.Select(e => e.UnitId).Distinct().Take(MaxUnits + 1).ToListAsync(cancellationToken);
                foreach (var id in ids) units[id] = null;
                break;
            }

            case SelectionSource.Kinds.PastedList:
            {
                var serials = source.Serials ?? throw new CommandRefusedException("source_invalid", "A pasted list needs serials.");
                foreach (var serial in serials.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ResolvedUnit? unit;
                    try { unit = await resolver.ResolveAsync(new DocumentSerial(serial), null, cancellationToken); }
                    catch (CommandRefusedException ex) when (ex.Rule == "serial_ambiguous") { rejected.Add($"{serial}: {ex.Message}"); continue; }
                    if (unit is null) unknown.Add(serial); else units[unit.UnitId] = unit.NormalisedValue;
                }

                break;
            }

            case SelectionSource.Kinds.SerialRange:
            {
                var scheme = schemes.Find(source.Scheme ?? "") ?? throw new CommandRefusedException("scheme_unknown", $"Serial scheme '{source.Scheme}' is not defined for this tenant.");
                var start = scheme.TryParse(source.RangeStart ?? "") ?? throw new CommandRefusedException("range_invalid", $"'{source.RangeStart}' is not a serial of scheme {scheme.Code}.");
                var end = scheme.TryParse(source.RangeEnd ?? "") ?? throw new CommandRefusedException("range_invalid", $"'{source.RangeEnd}' is not a serial of scheme {scheme.Code}.");
                if (start.Block != end.Block)
                {
                    throw new CommandRefusedException("range_crosses_block", $"{source.RangeStart} → {source.RangeEnd} crosses the {scheme.BlockFormat} boundary of scheme {scheme.Code}; a range expands within one block only.", "enter two ranges, or paste a list");
                }

                if (end.Counter < start.Counter) throw new CommandRefusedException("range_invalid", "The range end is before its start.");
                if (end.Counter - start.Counter + 1 > MaxUnits) throw new CommandRefusedException("range_too_large", $"A range of more than {MaxUnits:N0} serials cannot be a selection set.");
                var values = new List<string>();
                for (var c = start.Counter; c <= end.Counter; c++) values.Add(scheme.Format(start.Block, c).Replace(scheme.Separator, "", StringComparison.Ordinal).ToUpperInvariant());
                var scope = db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && i.Type == scheme.IdentifierType && i.ValidTo == null);
                var found = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var chunk in values.Chunk(2000))
                {
                    var rows = await scope.Where(i => chunk.Contains(i.NormalisedValue)).Select(i => new { i.NormalisedValue, i.UnitId }).ToListAsync(cancellationToken);
                    foreach (var r in rows) found[r.NormalisedValue] = r.UnitId;
                }

                foreach (var v in values)
                {
                    if (found.TryGetValue(v, out var id)) units[id] = v; else unknown.Add(v);
                }

                break;
            }

            case SelectionSource.Kinds.ImportJob:
            {
                var jobId = source.ImportJobId ?? throw new CommandRefusedException("source_invalid", "An import-job source needs a job id.");
                foreach (var row in await imports.ReadAsync(jobId, cancellationToken))
                {
                    if (row.UnitId is { } unitId) units[unitId] = row.NormalisedValue;
                    else if (!string.IsNullOrWhiteSpace(row.NormalisedValue)) unknown.Add(row.NormalisedValue);
                }
                break;
            }

            default:
                throw new CommandRefusedException("source_invalid", $"Selection source kind '{source.Kind}' is not known.");
        }

        if (units.Count > MaxUnits)
        {
            throw new CommandRefusedException("selection_too_large", $"The source resolves to more than {MaxUnits:N0} units; narrow it.");
        }

        var missingSerials = units.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList();
        if (missingSerials.Count > 0)
        {
            foreach (var chunk in missingSerials.Chunk(2000))
            {
                var rows = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && chunk.Contains(i.UnitId) && i.ValidTo == null && i.Type == IdentifierType.InternalSerial).Select(i => new { i.UnitId, i.NormalisedValue }).ToListAsync(cancellationToken);
                foreach (var r in rows) units[r.UnitId] = r.NormalisedValue;
            }
        }

        var selection = new SelectionSet
        {
            TenantId = tenantId, SelectionId = command.CommandId, SourceKind = source.Kind, SourceJson = JsonSerializer.Serialize(source, LedgerJson.Options),
            ResolvedCount = units.Count, UnknownCount = unknown.Count + rejected.Count, CreatedAt = clock.UtcNow, CreatedBy = command.ActorRef,
        };
        db.SelectionSets.Add(selection);
        db.SelectionSetMembers.AddRange(units.Select(kv => new SelectionSetMember { TenantId = tenantId, SelectionId = selection.SelectionId, UnitId = kv.Key, Serial = kv.Value }));
        return new ResolveSelectionSetResult(selection.SelectionId, units.Count, unknown, rejected, source);
    }

    private async Task<IReadOnlyList<Guid>> ResolveLegacyBaseOrdersAsync(
        TenantId tenantId,
        DocumentRef document,
        DateTime? before,
        CancellationToken cancellationToken)
    {
        const string source = """
            SELECT TOP (@take) candidate.unit_id AS UnitId
            FROM
            (
                SELECT event.unit_id
                FROM serialisation.unit_event AS event
                WHERE event.tenant_id = @tenant
                  AND event.event_time < @before
                  AND JSON_VALUE(event.attributes, '$.base_order.type') = @document_type
                  AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(event.attributes, '$.base_order.id')) = @document_id
                  AND (@document_line IS NULL
                       OR TRY_CONVERT(int, JSON_VALUE(event.attributes, '$.base_order.line')) = @document_line)
                UNION
                SELECT event.unit_id
                FROM serialisation.unit_event_archive AS event
                WHERE event.tenant_id = @tenant
                  AND event.event_time < @before
                  AND JSON_VALUE(event.attributes, '$.base_order.type') = @document_type
                  AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(event.attributes, '$.base_order.id')) = @document_id
                  AND (@document_line IS NULL
                       OR TRY_CONVERT(int, JSON_VALUE(event.attributes, '$.base_order.line')) = @document_line)
            ) AS candidate
            ORDER BY candidate.unit_id
            """;
        return await db.Database.SqlQueryRaw<LegacyBaseOrderUnit>(source,
                new SqlParameter("@take", MaxUnits + 1),
                new SqlParameter("@tenant", tenantId.Value),
                new SqlParameter("@before", before ?? DateTime.MaxValue),
                new SqlParameter("@document_type", document.Type),
                new SqlParameter("@document_id", document.Id),
                new SqlParameter("@document_line", (object?)document.Line ?? DBNull.Value))
            .Select(row => row.UnitId)
            .ToListAsync(cancellationToken);
    }

}
