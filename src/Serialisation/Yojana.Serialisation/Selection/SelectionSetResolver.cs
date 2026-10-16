using System.Text.Json;
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
/// is every unit the document's events touched; a customer is every unit the party currently owns; an item is every unit
/// whose current assignment is that item; a date range is every unit with an event in it; a pasted list resolves serial by
/// serial and reports the unknown ones; a serial range expands within the scheme's block (a boundary-crossing range is
/// rejected, never partially expanded) and reports serials that are not units yet.
/// </summary>
public sealed class SelectionSetResolver(SerialisationDbContext db, UnitResolver resolver, ISerialSchemeSource schemes, ITenantContext tenant, IClock clock)
{
    public const int MaxUnits = 100_000;

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
                var ids = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == doc.Type && e.DocumentId == doc.Id && (doc.Line == null || e.DocumentLine == doc.Line)).Select(e => e.UnitId).Distinct().Take(MaxUnits + 1).ToListAsync(cancellationToken);
                foreach (var id in ids) units[id] = null;
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
}
