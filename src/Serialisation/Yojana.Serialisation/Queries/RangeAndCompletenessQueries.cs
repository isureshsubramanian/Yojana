using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Queries;

/// <summary>Serial ranges of a scheme for the allocation screen, server-paged, newest first.</summary>
public sealed class SerialRangeQueries(SerialisationDbContext db, ISerialSchemeSource schemes, ITenantContext tenant)
{
    private static readonly SortedPage<SerialRange, Guid> Pages = new("reserved", row => row.RangeId,
        new SortColumn<SerialRange, long>("range", row => row.StartOrdinal ?? long.MinValue),
        new SortColumn<SerialRange, DateTime>("reserved", row => row.ReservedAt, SortDirection.Descending),
        new SortColumn<SerialRange, string>("status", row => row.Status));

    public IReadOnlyList<SerialSchemeRow> Schemes() => schemes.All().Select(s => new SerialSchemeRow(s.Code, s.IdentifierType, s.Prefix, s.BlockFormat, s.Separator, s.CounterDigits, s.Format(s.BlockFor(DateTime.UtcNow), 1))).ToList();

    public async Task<SerialRangeList> ListAsync(string scheme, string? status, string? sort, string? direction,
        int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var paging = Pages.Parse(sort, direction, take, skip: skip);
        var query = db.SerialRanges.AsNoTracking().Where(r => r.TenantId == tenantId && r.Scheme == scheme);
        if (status is not null) query = query.Where(r => r.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var rows = await paging.Apply(query).Take(paging.Take)
            .Select(r => new SerialRangeRow(r.RangeId, r.Scheme, r.StartValue, r.EndValue, r.Count, r.CommissionedCount, r.Status, r.RandomisedListRef != null, r.AllocatedToDocumentType, r.AllocatedToDocumentId, r.ItemId, r.ReservedAt, r.ReservedBy, r.Note)).ToListAsync(cancellationToken);
        var all = db.SerialRanges.AsNoTracking().Where(r => r.TenantId == tenantId && r.Scheme == scheme);
        var issued = await all.SumAsync(r => (long)r.Count, cancellationToken);
        var definition = schemes.Find(scheme);
        string? nextFree = null;
        if (definition is not null)
        {
            var block = definition.BlockFor(DateTime.UtcNow);
            var blockStart = SerialSchemeDefinition.OrdinalOf(block, 0);
            var blockEnd = SerialSchemeDefinition.OrdinalOf(block, definition.MaxCounter);
            var maxEnd = await all.Where(r => r.EndOrdinal >= blockStart && r.EndOrdinal <= blockEnd).MaxAsync(r => r.EndOrdinal, cancellationToken);
            nextFree = definition.FormatOrdinal(maxEnd is { } e ? e + 1 : blockStart);
        }

        return new SerialRangeList(new Page<SerialRangeRow>(rows, total, paging.Skip, paging.Take), scheme, await all.CountAsync(cancellationToken), issued, nextFree);
    }
}

/// <summary>The As-built completeness screen's query: by serial list, selection set or document, one row per unit and one column per slot.</summary>
public sealed class CompletenessQueries(SerialisationDbContext db, AsBuiltCompleteness completeness, UnitResolver resolver, ITenantContext tenant)
{
    public async Task<CompletenessResult> QueryAsync(CompletenessQuery query, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unknown = new List<string>();
        var units = new List<(Guid UnitId, string? Serial)>();
        if (query.Serials is { Count: > 0 })
        {
            foreach (var serial in query.Serials.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var unit = await resolver.ResolveAsync(new DocumentSerial(serial), null, cancellationToken);
                if (unit is null) unknown.Add(serial); else units.Add((unit.UnitId, unit.NormalisedValue));
            }
        }
        else if (query.SelectionId is { } selectionId)
        {
            units = (await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.SelectionId == selectionId).OrderBy(m => m.Serial).Select(m => new { m.UnitId, m.Serial }).ToListAsync(cancellationToken)).Select(m => (m.UnitId, m.Serial)).ToList();
        }
        else if (query.Document is { } doc)
        {
            var ids = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == doc.Type && e.DocumentId == doc.Id).Select(e => e.UnitId).Distinct().ToListAsync(cancellationToken);
            var serials = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && ids.Contains(i.UnitId) && i.ValidTo == null && i.Type == IdentifierType.InternalSerial).ToDictionaryAsync(i => i.UnitId, i => i.NormalisedValue, cancellationToken);
            units = ids.Select(id => (id, serials.TryGetValue(id, out var s) ? s : null)).OrderBy(u => u.Item2).ToList();
        }

        var take = Math.Clamp(query.Take, 1, 200);
        var items = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && units.Select(u => u.UnitId).Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId, c => c.ItemId, cancellationToken);
        var pairs = units.Where(u => items.ContainsKey(u.UnitId)).Select(u => (u.UnitId, items[u.UnitId])).ToList();
        var reports = pairs.Count == 0 ? [] : await completeness.QueryAsync(pairs, query.EffectiveAt ?? DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
        var rows = reports.Select(r => new CompletenessRow(r.UnitId, units.First(u => u.UnitId == r.UnitId).Serial, r.ItemId, r.Policy, r.Slots.Count, r.Slots.Count(s => s.Outcome == SlotStatus.Complete), r.MissingRequiredComponentSerials, r.UnexpectedCount, r.IsComplete,
            r.Slots.Select(s => { var first = s.Captured.Count > 0 ? s.Captured[0] : null; return new CompletenessSlot(s.Slot, s.ExpectedItemCode, s.ExpectedItemId, s.Outcome, first?.Serial, first?.ChildUnitId, first?.ChildItemId); }).ToList())).ToList();
        var slots = rows.SelectMany(r => r.Slots.Select(s => s.Slot)).Distinct().OrderBy(s => s).ToList();
        return new CompletenessResult(new Page<CompletenessRow>(rows.Skip(query.Skip).Take(take).ToList(), rows.Count, query.Skip, take), rows.Count, rows.Count(r => r.Complete), rows.Count(r => r.Missing > 0), rows.Sum(r => r.Missing), rows.Count(r => r.Unexpected > 0), slots, unknown);
    }
}
