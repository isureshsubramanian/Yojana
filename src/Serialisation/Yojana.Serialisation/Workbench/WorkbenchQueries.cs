using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.Workbench;

/// <summary>Resolves each entry through P1's identifier lookup or stored selection-set service.</summary>
public sealed class WorkbenchEntryResolver(
    SerialisationDbContext db,
    UnitQueries units,
    SelectionSetResolver selections,
    ITenantContext tenant)
{
    public async Task<WorkbenchResolution> ResolveAsync(ResolveWorkbenchEntry command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        IReadOnlyList<Guid> unitIds;
        string? resolution = null;
        Guid? selectionId = command.Entry.SelectionId;

        switch (command.Entry.Kind)
        {
            case WorkbenchTargetKind.Identifier:
            {
                if (string.IsNullOrWhiteSpace(command.Entry.Value)) throw new CommandRefusedException("identifier_required", "Identifier entry needs a value.");
                var lookup = await units.LookupAsync(command.Entry.Value, command.Entry.IdentifierType, cancellationToken);
                var identifierIds = lookup.Hits.Select(h => h.IdentifierId).ToList();
                var mergedFrom = identifierIds.Count == 0
                    ? new Dictionary<Guid, Guid?>()
                    : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && identifierIds.Contains(i.IdentifierId))
                        .ToDictionaryAsync(i => i.IdentifierId, i => i.MergedFromUnitId, cancellationToken);
                unitIds = lookup.Hits.Select(h => mergedFrom.GetValueOrDefault(h.IdentifierId) ?? h.UnitId).Distinct().ToList();
                resolution = lookup.Resolution;
                break;
            }
            case WorkbenchTargetKind.Unit:
                unitIds = command.Entry.UnitId is { } unitId ? [unitId] : throw new CommandRefusedException("unit_required", "Unit entry needs a unit id.");
                break;
            case WorkbenchTargetKind.DocumentLine:
            {
                var document = command.Entry.Document ?? throw new CommandRefusedException("document_required", "Document-line entry needs a document reference.");
                var resolved = await selections.ResolveAsync(new ResolveSelectionSet(command.ResolutionId, SelectionSource.ByDocument(document), command.ActorRef), cancellationToken);
                selectionId = resolved.SelectionId;
                unitIds = await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.SelectionId == resolved.SelectionId).Select(m => m.UnitId).ToListAsync(cancellationToken);
                resolution = "selection_set";
                break;
            }
            case WorkbenchTargetKind.SelectionSet:
            {
                var selected = command.Entry.SelectionId ?? throw new CommandRefusedException("selection_required", "Selection-set entry needs a selection id.");
                if (!await db.SelectionSets.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && s.SelectionId == selected, cancellationToken))
                    throw new CommandRefusedException("selection_unknown", $"Selection {selected} does not exist.");
                unitIds = await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.SelectionId == selected).Select(m => m.UnitId).ToListAsync(cancellationToken);
                resolution = "selection_set";
                break;
            }
            default:
                throw new CommandRefusedException("workbench_entry_invalid", $"Workbench entry kind '{command.Entry.Kind}' is not known.");
        }

        var original = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && unitIds.Contains(u.UnitId))
            .Select(u => new { u.UnitId, u.MergedIntoUnitId }).ToListAsync(cancellationToken);
        var resolvedIds = original.Select(u => u.MergedIntoUnitId ?? u.UnitId).Distinct().ToList();
        var state = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && resolvedIds.Contains(c.UnitId))
            .Select(c => new { c.UnitId, c.ItemId, c.LifecycleStatus }).ToDictionaryAsync(c => c.UnitId, cancellationToken);
        var identifiers = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && resolvedIds.Contains(i.UnitId) && i.ValidTo == null)
            .OrderBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(i => i.Type)
            .Select(i => new { i.UnitId, i.NormalisedValue }).ToListAsync(cancellationToken);
        var display = identifiers.GroupBy(i => i.UnitId).ToDictionary(g => g.Key, g => g.First().NormalisedValue);
        var matchKind = command.Entry.Kind == WorkbenchTargetKind.Identifier ? resolution : command.Entry.Kind;
        var candidates = original.Select(u =>
        {
            var resolvedId = u.MergedIntoUnitId ?? u.UnitId;
            state.TryGetValue(resolvedId, out var current);
            return new WorkbenchCandidate(u.UnitId, resolvedId, u.MergedIntoUnitId is not null,
                current?.LifecycleStatus ?? (u.MergedIntoUnitId is not null ? LifecycleStatus.Merged : "unknown"), current?.ItemId,
                display.GetValueOrDefault(resolvedId), matchKind);
        }).OrderBy(c => c.DisplayIdentifier).ThenBy(c => c.ResolvedUnitId).ToList();
        return new WorkbenchResolution(command.Entry, candidates, selectionId, resolution);
    }
}

/// <summary>Set-oriented reads for the state and lineage panel; every collection is loaded in one tenant-led query.</summary>
public sealed class WorkbenchLineageQueries(SerialisationDbContext db, UnitQueries units, ITenantContext tenant)
{
    public async Task<WorkbenchUnitLineage?> ReadAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var state = await units.DetailAsync(unitId, cancellationToken);
        if (state is null) return null;
        if (state.MergedIntoUnitId is { } survivor)
        {
            state = await units.DetailAsync(survivor, cancellationToken);
            if (state is null) return null;
            unitId = survivor;
        }

        var tenantId = tenant.TenantId;
        var assignments = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == unitId)
            .OrderByDescending(a => a.ValidFrom)
            .Select(a => new WorkbenchItemAssignment(a.AssignmentId, a.ItemId, a.ModelVersionId, a.ValidFrom, a.ValidTo, a.Reason))
            .ToListAsync(cancellationToken);
        var entitlements = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId)
            .OrderBy(e => e.CoverageFamily).ThenByDescending(e => e.CreatedAt)
            .Select(e => new WorkbenchEntitlement(e.EntitlementId, e.CoverageFamily, e.Type, e.Direction, e.Status, e.StartDate, e.EndDate, e.SupersedesEntitlementId, e.ParentEntitlementId))
            .ToListAsync(cancellationToken);
        var assemblyRows = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && (l.ParentUnitId == unitId || l.ChildUnitId == unitId))
            .OrderBy(l => l.RemovedAt == null ? 0 : 1).ThenBy(l => l.InstalledAt)
            .Select(l => new { l.LinkId, l.ParentUnitId, l.ChildUnitId, l.ChildItemId, l.Relation, l.Slot, l.InstalledAt, l.RemovedAt })
            .ToListAsync(cancellationToken);
        var assembly = assemblyRows.Select(l => new WorkbenchAssemblyLink(l.LinkId, l.ParentUnitId, l.ChildUnitId, l.ChildItemId, l.Relation, l.Slot,
            l.InstalledAt, l.RemovedAt, l.ParentUnitId == unitId ? "child" : "parent")).ToList();
        var merges = await db.UnitMerges.AsNoTracking().Where(m => m.TenantId == tenantId && (m.SurvivorUnitId == unitId || m.AbsorbedUnitId == unitId))
            .OrderByDescending(m => m.MergedAt)
            .Select(m => new WorkbenchMergeHistory(m.CorrectionId, m.SurvivorUnitId, m.AbsorbedUnitId, m.MergedAt, m.ReversedByCorrectionId, m.ReversedAt))
            .ToListAsync(cancellationToken);
        var correctionIds = await db.CorrectionUnits.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId == unitId).Select(c => c.CorrectionId).ToListAsync(cancellationToken);
        var corrections = await db.Corrections.AsNoTracking().Where(c => c.TenantId == tenantId && correctionIds.Contains(c.CorrectionId))
            .OrderByDescending(c => c.RecordedTime)
            .Select(c => new WorkbenchCorrectionHistory(c.CorrectionId, c.Command, c.ReasonCode, c.InitiatedVia, c.InitiatingRole, c.ActorRef,
                c.EventTime, c.RecordedTime, c.CorrectsCorrectionId, c.Note))
            .ToListAsync(cancellationToken);
        var history = await units.HistoryAsync(unitId, 0, 10, cancellationToken);
        return new WorkbenchUnitLineage(unitId, state, state.Identifiers, assignments, state.Episodes, entitlements, assembly, merges, corrections, history.Items);
    }
}
