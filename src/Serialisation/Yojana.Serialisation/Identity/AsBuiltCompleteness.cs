using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Settings;

namespace Yojana.Serialisation.Identity;

/// <summary>One expected slot of a unit against what is captured there.</summary>
public sealed record SlotStatus(string Slot, Guid ExpectedItemId, string ExpectedItemCode, int ExpectedQuantity, IReadOnlyList<CapturedChild> Captured, string Outcome)
{
    public const string Complete = "complete";
    public const string Missing = "missing";
    public const string UnexpectedItem = "unexpected_item";
}

public sealed record CapturedChild(Guid LinkId, Guid? ChildUnitId, Guid? ChildItemId, string? Serial);

/// <summary>A unit's completeness: expected serialised slots, captured children, and children in no expected slot.</summary>
public sealed record UnitCompleteness(Guid UnitId, Guid ItemId, IReadOnlyList<SlotStatus> Slots, IReadOnlyList<CapturedChild> Unexpected, string Policy)
{
    public int MissingRequiredComponentSerials => Slots.Count(s => s.Outcome == SlotStatus.Missing);

    public int UnexpectedCount => Slots.Count(s => s.Outcome == SlotStatus.UnexpectedItem) + Unexpected.Count;

    public bool IsComplete => MissingRequiredComponentSerials == 0 && UnexpectedCount == 0;
}

/// <summary>
/// Identity rule 12 (cases 121, 134): expected serialised components come from the BOM through <see cref="IBomQueryPort"/>
/// (phantoms expanded, effective at the production-order date); missing = expected minus open AssemblyLink children carrying
/// an identifier. The standing query answers "by serial or list" one row per unit and one column per slot, which is what the
/// As-built completeness screen shows. The item class policy decides whether a shortfall blocks or warns.
/// </summary>
public sealed class AsBuiltCompleteness(SerialisationDbContext db, IBomQueryPort bom, IItemQueryPort items,
    ITenantContext tenant, ErpBomCompletenessSettings settings)
{
    public async Task<UnitCompleteness> CheckAsync(Guid unitId, Guid itemId, DateOnly effectiveAt, CancellationToken cancellationToken) =>
        (await QueryAsync([(unitId, itemId)], effectiveAt, cancellationToken))[0];

    /// <summary>Completeness for a list of units (the "by serial or list" query). Units of one item share one BOM read.</summary>
    public async Task<IReadOnlyList<UnitCompleteness>> QueryAsync(IReadOnlyList<(Guid UnitId, Guid ItemId)> units, DateOnly effectiveAt, CancellationToken cancellationToken)
        => await QueryAsync(units.Select(unit => (unit.UnitId, unit.ItemId, effectiveAt)).ToList(), cancellationToken);

    /// <summary>Completeness for units whose governing BOM dates may differ, with one set-based read per dependency.</summary>
    public async Task<IReadOnlyList<UnitCompleteness>> QueryAsync(
        IReadOnlyList<(Guid UnitId, Guid ItemId, DateOnly EffectiveAt)> units, CancellationToken cancellationToken)
    {
        if (!await settings.EnabledAsync(cancellationToken))
        {
            var disabledProfiles = await items.GetManyAsync(units.Select(unit => unit.ItemId).Distinct().ToList(),
                cancellationToken);
            return units.Select(unit => new UnitCompleteness(unit.UnitId, unit.ItemId, [], [],
                disabledProfiles.GetValueOrDefault(unit.ItemId)?.AsBuiltCompleteness
                    ?? ItemSerialisationProfile.Completeness.Block)).ToList();
        }
        var tenantId = tenant.TenantId;
        var unitIds = units.Select(u => u.UnitId).ToList();
        var links = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && unitIds.Contains(l.ParentUnitId) && l.RemovedAt == null).ToListAsync(cancellationToken);
        var childUnitIds = links.Where(l => l.ChildUnitId != null).Select(l => l.ChildUnitId!.Value).Distinct().ToList();
        var childItems = childUnitIds.Count == 0 ? new Dictionary<Guid, Guid>() : await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && childUnitIds.Contains(a.UnitId) && a.ValidTo == null).ToDictionaryAsync(a => a.UnitId, a => a.ItemId, cancellationToken);
        var childSerials = childUnitIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && childUnitIds.Contains(i.UnitId) && i.ValidTo == null && i.Type == IdentifierType.InternalSerial).ToDictionaryAsync(i => i.UnitId, i => i.NormalisedValue, cancellationToken);
        var itemIds = units.Select(unit => unit.ItemId).Distinct().ToList();
        var profiles = await items.GetManyAsync(itemIds, cancellationToken);
        var bomQueries = units.Select(unit => new ExpectedComponentQuery(unit.ItemId, unit.EffectiveAt)).Distinct().ToList();
        var boms = await bom.ExpectedSerialisedComponentsAsync(bomQueries, cancellationToken);
        var result = new List<UnitCompleteness>(units.Count);
        foreach (var (unitId, itemId, effectiveAt) in units)
        {
            var expected = boms.GetValueOrDefault(new ExpectedComponentQuery(itemId, effectiveAt)) ?? [];
            var policy = profiles.GetValueOrDefault(itemId)?.AsBuiltCompleteness ?? ItemSerialisationProfile.Completeness.Block;

            var mine = links.Where(l => l.ParentUnitId == unitId).ToList();
            var used = new HashSet<Guid>();
            var slots = new List<SlotStatus>();
            foreach (var slot in expected)
            {
                // A slot is satisfied by serialised children of the expected item; a serialised child of another item in that slot is an unexpected item.
                var inSlot = mine.Where(l => l.ChildUnitId != null && string.Equals(l.Slot, slot.Slot, StringComparison.OrdinalIgnoreCase)).ToList();
                var captured = inSlot.Select(l => new CapturedChild(l.LinkId, l.ChildUnitId, childItems.TryGetValue(l.ChildUnitId!.Value, out var ci) ? ci : null, childSerials.TryGetValue(l.ChildUnitId!.Value, out var s) ? s : null)).ToList();
                foreach (var l in inSlot) used.Add(l.LinkId);
                var ofItem = captured.Count(c => c.ChildItemId == slot.ComponentItemId);
                var outcome = captured.Any(c => c.ChildItemId != slot.ComponentItemId) ? SlotStatus.UnexpectedItem : ofItem >= slot.Quantity ? SlotStatus.Complete : SlotStatus.Missing;
                slots.Add(new SlotStatus(slot.Slot, slot.ComponentItemId, slot.ComponentItemCode, slot.Quantity, captured, outcome));
            }

            // Serialised children outside every expected slot (slot-less or an unknown slot name) are reported, never silently accepted.
            var unexpected = mine.Where(l => l.ChildUnitId != null && !used.Contains(l.LinkId) && expected.Count > 0)
                .Select(l => new CapturedChild(l.LinkId, l.ChildUnitId, childItems.TryGetValue(l.ChildUnitId!.Value, out var ci) ? ci : null, childSerials.TryGetValue(l.ChildUnitId!.Value, out var s) ? s : null)).ToList();
            result.Add(new UnitCompleteness(unitId, itemId, slots, unexpected, policy));
        }

        return result;
    }
}

/// <summary>Identity rule 12 at release: `missing_required_component_serials = 0`, or the posting fails with `incomplete_asbuilt` under the block policy.</summary>
public sealed class ReleaseCompletenessGuard(AsBuiltCompleteness completeness) : IReleaseGuard
{
    public async Task<CommandRefusedException?> CheckAsync(Unit unit, Guid itemId, DateTime eventTime, CancellationToken cancellationToken)
    {
        var report = await completeness.CheckAsync(unit.UnitId, itemId, DateOnly.FromDateTime(eventTime), cancellationToken);
        if (report.IsComplete || report.Policy != ItemSerialisationProfile.Completeness.Block)
        {
            return null;
        }

        var missing = report.Slots.Where(s => s.Outcome == SlotStatus.Missing).Select(s => $"{s.Slot} {s.ExpectedItemCode}");
        var wrong = report.Slots.Where(s => s.Outcome == SlotStatus.UnexpectedItem).Select(s => $"{s.Slot} has an unexpected item");
        return new CommandRefusedException("incomplete_asbuilt",
            $"Unit {unit.UnitId}: {report.MissingRequiredComponentSerials} required component serial(s) missing ({string.Join(", ", missing.Concat(wrong))}).",
            "record the missing component serials with RecordAssembly, or correct the as-built (P3)");
    }
}
