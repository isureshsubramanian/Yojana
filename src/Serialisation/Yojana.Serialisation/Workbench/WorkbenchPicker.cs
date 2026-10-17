using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Workbench;

/// <summary>Read-only picker projection over tenant wording and current unit facts.</summary>
public sealed class WorkbenchPickerService(
    SerialisationDbContext db,
    IWorkbenchChoiceCatalog catalog,
    ITenantContext tenant)
{
    public async Task<WorkbenchPicker> QueryAsync(Guid? unitId, string? search, CancellationToken cancellationToken)
    {
        var configured = await catalog.GetChoicesAsync(cancellationToken);
        var fallback = configured.Single(c => c.Fallback);
        var shown = configured.Where(c => !c.Fallback).ToList();
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (term is not null)
            shown = shown.Where(c => c.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (c.SearchWords ?? []).Any(word => word.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();

        var suggestions = new List<WorkbenchChoiceSuggestion>();
        if (unitId is { } selectedUnit)
        {
            var facts = await FactsAsync(selectedUnit, cancellationToken);
            foreach (var suggested in WorkbenchSuggestionRules.Suggest(facts))
                if (configured.FirstOrDefault(c => c.Choice == suggested.Choice) is { } choice)
                    suggestions.Add(new(choice.Choice, suggested.Rule, choice.Label));
        }

        var groups = shown.OrderBy(c => c.Order).ThenBy(c => c.Label, StringComparer.Ordinal)
            .GroupBy(c => c.Area).Select(g => new WorkbenchChoiceGroup(g.Key, g.ToList())).ToList();
        return new(term, suggestions, groups, fallback);
    }

    private async Task<WorkbenchSuggestionFacts> FactsAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var current = await db.Current.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UnitId == unitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {unitId} does not exist.");
        var flags = JsonSerializer.Deserialize<List<string>>(current.OverlayFlags, LedgerJson.Options) ?? [];
        var duplicate = await db.Identifiers.AsNoTracking().AnyAsync(i => i.TenantId == tenantId && i.UnitId == unitId
            && i.Type == IdentifierType.VendorSerial && i.ValidTo == null && i.DuplicateAcknowledged, cancellationToken);
        // An open item-only child is the persisted signal that an assembly position exists without a captured unit.
        var uncaptured = await db.AssemblyLinks.AsNoTracking().AnyAsync(a => a.TenantId == tenantId && a.ParentUnitId == unitId
            && a.RemovedAt == null && a.ChildUnitId == null && a.ChildItemId != null, cancellationToken);
        return new(flags.Contains(OverlayFlag.DocumentMismatch), flags.Contains(OverlayFlag.AwaitingVendorSerial), duplicate, uncaptured);
    }
}
