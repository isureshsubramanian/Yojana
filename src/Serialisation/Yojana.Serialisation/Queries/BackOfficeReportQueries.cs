using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Reports;

/// <summary>
/// Internal reports over Serialisation-owned projections. Every page uses a keyset and every enrichment crosses a
/// batch port; no unit in a page can cause its own database or remote query.
/// </summary>
public sealed class BackOfficeReportQueries(
    SerialisationDbContext db,
    ITenantContext tenant,
    IItemNamePort items,
    IPartyNamePort parties,
    ILocationNamePort locations,
    IDocumentNumberPort documentNumbers,
    IInternalSaleDocumentQueryPort saleDocuments,
    AsBuiltCompleteness completeness,
    UnitQueries unitQueries)
{
    public const int MaximumPageRoots = 500;
    public const int MaximumExpandedRowsPerPage = 10_000;
    public const int MaximumTreeDepth = 32;
    public const int MaximumExportRows = 100_000;

    private sealed class InventorySeed
    {
        public Guid UnitId { get; set; }
        public Guid ItemId { get; set; }
        public string? Serial { get; set; }
        public Guid? OwnerPartyRef { get; set; }
        public Guid? LocationId { get; set; }
        public string? LocationKind { get; set; }
        public required string LifecycleStatus { get; set; }
        public required string Disposition { get; set; }
        public string? ConditionGrade { get; set; }
    }

    private sealed record TreeNode(Guid RootUnitId, Guid UnitId, Guid? ParentUnitId, int Depth, string? Slot);

    public async Task<IdentifierCrossReferencePage> IdentifierCrossReferenceAsync(
        Guid selectionId, string? cursor, int take, CancellationToken cancellationToken)
    {
        var roots = await SelectionRootsAsync(selectionId, cursor, take, cancellationToken);
        var tree = await ExpandAsync(roots.Ids, cancellationToken);
        var unitIds = tree.Select(row => row.UnitId).Distinct().ToList();
        var state = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && unitIds.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.ItemId, row.OwnerPartyRef })
            .ToListAsync(cancellationToken);
        var byUnit = state.ToDictionary(row => row.UnitId);
        var identifiers = await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && unitIds.Contains(row.UnitId) && row.ValidTo == null)
            .OrderBy(row => row.Type).ThenBy(row => row.RecordedTime)
            .Select(row => new { row.UnitId, row.Type, row.NormalisedValue })
            .ToListAsync(cancellationToken);
        var identifiersByUnit = identifiers.GroupBy(row => row.UnitId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var itemNames = await items.NamesAsync(state.Select(row => row.ItemId).Distinct().ToList(), cancellationToken);
        var ownerNames = await parties.NamesAsync(state.Where(row => row.OwnerPartyRef != null)
            .Select(row => row.OwnerPartyRef!.Value).Distinct().ToList(), cancellationToken);

        string Primary(Guid unitId) => Identifier(unitId, IdentifierType.InternalSerial)
            ?? identifiersByUnit.GetValueOrDefault(unitId)?.FirstOrDefault()?.NormalisedValue
            ?? "IDENTIFIER PENDING";
        string? Identifier(Guid unitId, string type)
        {
            var values = identifiersByUnit.GetValueOrDefault(unitId)?.Where(row => row.Type == type)
                .Select(row => row.NormalisedValue).Distinct(StringComparer.Ordinal).ToList();
            return values is { Count: > 0 } ? string.Join("; ", values) : null;
        }

        var result = new List<IdentifierCrossReferenceRow>(tree.Count);
        foreach (var node in tree)
        {
            if (!byUnit.TryGetValue(node.UnitId, out var current)) continue;
            result.Add(new IdentifierCrossReferenceRow(
                Primary(node.RootUnitId),
                node.ParentUnitId is { } parent ? Primary(parent) : null,
                node.Depth,
                node.Slot,
                Identifier(node.UnitId, IdentifierType.InternalSerial) ?? "IDENTIFIER PENDING",
                Identifier(node.UnitId, IdentifierType.ManufacturerSerial),
                Identifier(node.UnitId, IdentifierType.VendorSerial),
                Identifier(node.UnitId, IdentifierType.CustomerSerial),
                Identifier(node.UnitId, IdentifierType.CustomerAssetTag),
                ItemName(itemNames.GetValueOrDefault(current.ItemId)),
                current.OwnerPartyRef is { } owner ? PartyName(ownerNames.GetValueOrDefault(owner)) : null));
        }

        return new IdentifierCrossReferencePage(result, roots.NextCursor, roots.Ids.Count);
    }

    public async Task<InstalledBaseStockPage> InstalledBaseAndStockAsync(
        InstalledBaseStockFilter filter, string? cursor, int take, CancellationToken cancellationToken)
    {
        if (!BackOfficeHolding.Values.Contains(filter.Holding))
            throw new CommandRefusedException("holding_unknown", $"Holding '{filter.Holding}' is not known.");
        take = Math.Clamp(take, 1, MaximumPageRoots);
        var query = db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (filter.ItemIds is { Count: > 0 }) query = query.Where(row => filter.ItemIds.Contains(row.ItemId));
        if (filter.LocationId is { } locationId) query = query.Where(row => row.LocationId == locationId);
        if (filter.Holding == BackOfficeHolding.InstalledBase) query = query.Where(row => row.OwnerPartyRef != null);
        if (filter.Holding == BackOfficeHolding.Stock)
            query = query.Where(row => row.OwnerPartyRef == null && row.CustodyPartyRef == null && row.LocationId != null);
        if (DecodePair(cursor) is { } after)
            query = query.Where(row => row.ItemId.CompareTo(after.First) > 0
                || row.ItemId == after.First && row.UnitId.CompareTo(after.Second) > 0);

        var page = await query.OrderBy(row => row.ItemId).ThenBy(row => row.UnitId).Take(take + 1)
            .Select(row => new InventorySeed
            {
                UnitId = row.UnitId,
                ItemId = row.ItemId,
                OwnerPartyRef = row.OwnerPartyRef,
                LocationId = row.LocationId,
                LocationKind = row.LocationKind,
                LifecycleStatus = row.LifecycleStatus,
                Disposition = row.Disposition,
                ConditionGrade = row.ConditionGrade,
                Serial = db.Identifiers.Where(identifier => identifier.TenantId == tenant.TenantId
                        && identifier.UnitId == row.UnitId && identifier.ValidTo == null)
                    .OrderBy(identifier => identifier.Type == IdentifierType.InternalSerial ? 0 : 1)
                    .ThenBy(identifier => identifier.Type)
                    .Select(identifier => identifier.NormalisedValue).FirstOrDefault(),
            }).ToListAsync(cancellationToken);
        var more = page.Count > take;
        if (more) page.RemoveAt(page.Count - 1);
        var itemNames = await items.NamesAsync(page.Select(row => row.ItemId).Distinct().ToList(), cancellationToken);
        var ownerNames = await parties.NamesAsync(page.Where(row => row.OwnerPartyRef != null)
            .Select(row => row.OwnerPartyRef!.Value).Distinct().ToList(), cancellationToken);
        var locationNames = await locations.NamesAsync(page.Where(row => row.LocationId != null)
            .Select(row => row.LocationId!.Value).Distinct().ToList(), cancellationToken);
        var rows = page.Select(row => new InstalledBaseStockRow(
            row.Serial ?? "IDENTIFIER PENDING",
            ItemName(itemNames.GetValueOrDefault(row.ItemId)),
            row.OwnerPartyRef is null ? BackOfficeHolding.Stock : BackOfficeHolding.InstalledBase,
            row.OwnerPartyRef is { } owner ? PartyName(ownerNames.GetValueOrDefault(owner)) : null,
            row.LocationId is { } location ? LocationName(locationNames.GetValueOrDefault(location)) : null,
            row.LocationKind,
            row.LifecycleStatus,
            row.Disposition,
            row.ConditionGrade)).ToList();
        var last = more ? page[^1] : null;
        return new InstalledBaseStockPage(rows, last is null ? null : EncodePair(last.ItemId, last.UnitId));
    }

    public async Task<AsBuiltCompletenessPage> AsBuiltCompletenessAsync(
        Guid selectionId, string? cursor, int take, DateOnly effectiveAt, CancellationToken cancellationToken)
    {
        var roots = await SelectionRootsAsync(selectionId, cursor, take, cancellationToken);
        var state = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && roots.Ids.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.ItemId, row.OwnerPartyRef })
            .ToListAsync(cancellationToken);
        var serials = await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && roots.Ids.Contains(row.UnitId)
                && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .Select(row => new { row.UnitId, row.NormalisedValue })
            .ToDictionaryAsync(row => row.UnitId, row => row.NormalisedValue, cancellationToken);
        var reports = await completeness.QueryAsync(state.Select(row => (row.UnitId, row.ItemId)).ToList(), effectiveAt, cancellationToken);
        var componentItems = reports.SelectMany(report => report.Slots.Select(slot => slot.ExpectedItemId)
                .Concat(report.Slots.SelectMany(slot => slot.Captured).Where(row => row.ChildItemId != null).Select(row => row.ChildItemId!.Value))
                .Concat(report.Unexpected.Where(row => row.ChildItemId != null).Select(row => row.ChildItemId!.Value)))
            .Concat(state.Select(row => row.ItemId)).Distinct().ToList();
        var itemNames = await items.NamesAsync(componentItems, cancellationToken);
        var ownerNames = await parties.NamesAsync(state.Where(row => row.OwnerPartyRef != null)
            .Select(row => row.OwnerPartyRef!.Value).Distinct().ToList(), cancellationToken);
        var customerRefs = await db.Episodes.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && roots.Ids.Contains(row.UnitId)
                && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
            .Select(row => new { row.UnitId, row.CustomerItemRef })
            .ToDictionaryAsync(row => row.UnitId, row => row.CustomerItemRef, cancellationToken);
        var stateByUnit = state.ToDictionary(row => row.UnitId);

        var result = new List<AsBuiltCompletenessRoot>(reports.Count);
        foreach (var report in reports.OrderBy(row => roots.Ids.IndexOf(row.UnitId)))
        {
            var componentRows = new List<AsBuiltComponentReportRow>();
            foreach (var slot in report.Slots)
            {
                foreach (var captured in slot.Captured)
                {
                    var itemId = captured.ChildItemId ?? slot.ExpectedItemId;
                    componentRows.Add(new AsBuiltComponentReportRow(slot.Slot,
                        ItemName(itemNames.GetValueOrDefault(itemId)), captured.Serial, slot.Outcome));
                }
                var ofExpectedItem = slot.Captured.Count(row => row.ChildItemId == slot.ExpectedItemId);
                for (var missing = ofExpectedItem; missing < slot.ExpectedQuantity; missing++)
                    componentRows.Add(new AsBuiltComponentReportRow(slot.Slot,
                        ItemName(itemNames.GetValueOrDefault(slot.ExpectedItemId)), null, SlotStatus.Missing));
            }
            foreach (var unexpected in report.Unexpected)
            {
                componentRows.Add(new AsBuiltComponentReportRow(unexpected.ChildItemId is null ? "UNASSIGNED" : "UNEXPECTED",
                    unexpected.ChildItemId is { } itemId ? ItemName(itemNames.GetValueOrDefault(itemId)) : "UNKNOWN PART",
                    unexpected.Serial, SlotStatus.UnexpectedItem));
            }
            var current = stateByUnit[report.UnitId];
            result.Add(new AsBuiltCompletenessRoot(
                serials.GetValueOrDefault(report.UnitId) ?? "IDENTIFIER PENDING",
                ItemName(itemNames.GetValueOrDefault(report.ItemId)),
                customerRefs.GetValueOrDefault(report.UnitId),
                current.OwnerPartyRef is { } owner ? PartyName(ownerNames.GetValueOrDefault(owner)) : null,
                report.IsComplete ? SlotStatus.Complete : SlotStatus.Missing,
                componentRows));
        }
        return new AsBuiltCompletenessPage(result, roots.NextCursor);
    }

    public async Task<InternalUnitLookup?> InternalLookupAsync(
        string identifier, string? identifierType, CancellationToken cancellationToken)
    {
        var resolved = await unitQueries.LookupAsync(identifier, identifierType, cancellationToken);
        var unitIds = resolved.Hits.Select(row => row.UnitId).Distinct().ToList();
        if (unitIds.Count == 0) return null;
        if (unitIds.Count != 1)
            throw new CommandRefusedException("identifier_ambiguous", "The identifier resolves to more than one unit; supply its identifier type.");
        var unitId = unitIds[0];
        var current = await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId)
            .Select(row => new { row.ItemId, row.LifecycleStatus, row.Disposition }).SingleOrDefaultAsync(cancellationToken);
        if (current is null) return null;
        var identifierRows = await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId)
            .OrderBy(row => row.ValidTo == null ? 0 : 1).ThenBy(row => row.Type).ThenByDescending(row => row.ValidFrom)
            .Select(row => new InternalIdentifier(row.Type, row.RawValue, row.ValidTo == null)).ToListAsync(cancellationToken);
        var ownership = await db.Episodes.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId && row.Kind == EpisodeKind.Ownership)
            .OrderByDescending(row => row.ValidFrom)
            .Select(row => new
            {
                row.EpisodeId, row.PartyRef, row.ValidFrom, row.ValidTo, row.CustomerPoRef, row.CustomerItemRef,
                Opening = row.OpeningDocumentType == null || row.OpeningDocumentId == null ? null
                    : new DocumentRef(row.OpeningDocumentType, row.OpeningDocumentId.Value, row.OpeningDocumentLine),
            }).ToListAsync(cancellationToken);
        var partyNames = await parties.NamesAsync(ownership.Where(row => row.PartyRef != null)
            .Select(row => row.PartyRef!.Value).Distinct().ToList(), cancellationToken);
        var chainFacts = await saleDocuments.ReadAsync(ownership.Where(row => row.Opening != null)
            .Select(row => new InternalSaleChainRequest(row.EpisodeId, row.Opening!)).ToList(), cancellationToken);
        var documents = chainFacts.SelectMany(row => new[] { row.Order, row.Delivery, row.Invoice }).Where(row => row != null)
            .Select(row => row!).ToList();
        var documentLabels = await documentNumbers.NumbersAsync(documents, cancellationToken);
        var allItemIds = chainFacts.SelectMany(row => row.WarrantyLines).Select(row => row.ItemId).Append(current.ItemId).Distinct().ToList();
        var itemNames = await items.NamesAsync(allItemIds, cancellationToken);
        var chainsByEpisode = chainFacts.GroupBy(row => row.OwnershipEpisodeId).ToDictionary(group => group.Key, group => group.ToList());
        var ownershipRows = ownership.Select(episode => new InternalOwnershipSale(
            episode.PartyRef is { } owner ? PartyName(partyNames.GetValueOrDefault(owner)) : "OWNER NOT RECORDED",
            episode.ValidFrom, episode.ValidTo, episode.ValidTo == null, episode.CustomerPoRef, episode.CustomerItemRef,
            chainsByEpisode.GetValueOrDefault(episode.EpisodeId)?.Select(chain => new InternalSaleChain(
                DocumentName(chain.Order, documentLabels), DocumentName(chain.Delivery, documentLabels),
                DocumentName(chain.Invoice, documentLabels), chain.DeliveredAt, chain.InvoicedAt,
                chain.WarrantyLines.Select(line => new InternalWarrantyLine(line.Line,
                    ItemName(itemNames.GetValueOrDefault(line.ItemId)), line.Price, line.Currency, line.PolicyCode,
                    line.InternalNote)).ToList(), chain.InternalNote)).ToList() ?? [])).ToList();
        var serviceHistory = await InternalServiceHistoryAsync(unitId, cancellationToken);
        var primary = identifierRows.FirstOrDefault(row => row.Current && row.Type == IdentifierType.InternalSerial)?.Value
            ?? identifierRows.FirstOrDefault(row => row.Current)?.Value ?? "IDENTIFIER PENDING";
        return new InternalUnitLookup(primary, ItemName(itemNames.GetValueOrDefault(current.ItemId)),
            current.LifecycleStatus, current.Disposition, identifierRows, ownershipRows, serviceHistory);
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportIdentifierCrossReferenceAsync(Guid selectionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var count = 0;
        do
        {
            var page = await IdentifierCrossReferenceAsync(selectionId, cursor, MaximumPageRoots, cancellationToken);
            foreach (var row in page.Items)
            {
                EnsureExportCap(++count);
                yield return [row.RootSerial, row.ParentSerial, row.Depth.ToString(CultureInfo.InvariantCulture), row.Slot,
                    row.InternalSerial, row.ManufacturerSerial, row.VendorSerial, row.CustomerSerial, row.AssetTag,
                    row.Item, row.CurrentOwner];
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportInstalledBaseAndStockAsync(InstalledBaseStockFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var count = 0;
        do
        {
            var page = await InstalledBaseAndStockAsync(filter, cursor, MaximumPageRoots, cancellationToken);
            foreach (var row in page.Items)
            {
                EnsureExportCap(++count);
                yield return [row.Serial, row.Item, row.Holding, row.CurrentOwner, row.Location, row.LocationKind,
                    row.LifecycleStatus, row.Disposition, row.Condition];
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportAsBuiltCompletenessAsync(Guid selectionId, DateOnly effectiveAt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var count = 0;
        do
        {
            var page = await AsBuiltCompletenessAsync(selectionId, cursor, MaximumPageRoots, effectiveAt, cancellationToken);
            foreach (var root in page.Items)
            {
                if (root.Components.Count == 0)
                {
                    EnsureExportCap(++count);
                    yield return [root.RootSerial, root.RootItem, root.CustomerItemReference, root.CurrentOwner,
                        null, null, null, root.Outcome];
                }
                foreach (var component in root.Components)
                {
                    EnsureExportCap(++count);
                    yield return [root.RootSerial, root.RootItem, root.CustomerItemReference, root.CurrentOwner,
                        component.Slot, component.ComponentItem, component.ComponentSerial, component.Outcome];
                }
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportInternalLookupAsync(string identifier, string? identifierType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lookup = await InternalLookupAsync(identifier, identifierType, cancellationToken)
            ?? throw new CommandRefusedException("unit_not_found", "No unit matches that identifier.");
        var count = 0;
        foreach (var ownership in lookup.Ownership)
        foreach (var chain in ownership.SaleChains.DefaultIfEmpty(new InternalSaleChain(null, null, null, null, null, [], null)))
        {
            EnsureExportCap(++count);
            yield return ["sale", lookup.Serial, lookup.Item, ownership.Owner,
                ownership.ValidFrom.ToString("O", CultureInfo.InvariantCulture), ownership.ValidTo?.ToString("O", CultureInfo.InvariantCulture),
                chain.Order, chain.Delivery, chain.Invoice, null, chain.InternalNote, null, null];
            foreach (var line in chain.WarrantyLines)
            {
                EnsureExportCap(++count);
                yield return ["warranty_line", lookup.Serial, line.Item, ownership.Owner, null, null,
                    chain.Order, chain.Delivery, chain.Invoice, line.PolicyCode, line.InternalNote,
                    line.Price.ToString(CultureInfo.InvariantCulture), line.Currency];
            }
        }
        foreach (var service in lookup.ServiceHistory)
        {
            EnsureExportCap(++count);
            yield return ["service", lookup.Serial, lookup.Item, null, service.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
                null, null, null, service.Reference, service.Kind, service.InternalNote,
                service.Cost?.ToString(CultureInfo.InvariantCulture), service.Currency];
        }
    }

    private async Task<IReadOnlyList<InternalServiceCost>> InternalServiceHistoryAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var claims = await (from line in db.ClaimLines.AsNoTracking()
                            join claim in db.Claims.AsNoTracking() on new { line.TenantId, line.ClaimId } equals new { claim.TenantId, claim.ClaimId }
                            where line.TenantId == tenant.TenantId && line.UnitId == unitId
                            select new InternalServiceCost("claim", claim.ClaimNumber, claim.Status, claim.SubmittedAt,
                                line.Description, line.ApprovedAmount != 0 ? line.ApprovedAmount : line.RequestedAmount, claim.Currency))
            .ToListAsync(cancellationToken);
        var rmas = await (from line in db.RmaLines.AsNoTracking()
                          join rma in db.Rmas.AsNoTracking() on new { line.TenantId, line.RmaId } equals new { rma.TenantId, rma.RmaId }
                          where line.TenantId == tenant.TenantId && (line.RequestedUnitId == unitId || line.AuthorisedUnitId == unitId
                              || line.ReceivedUnitId == unitId || line.ProvisionalUnitId == unitId)
                          select new InternalServiceCost("rma", rma.RmaNumber ?? "RMA PENDING", rma.Stage, rma.RequestedAt,
                              line.Description, line.ApprovedAmount, rma.Currency)).ToListAsync(cancellationToken);
        var refurbishment = await (from cost in db.RefurbishmentCosts.AsNoTracking()
                                   join work in db.RefurbishmentWorkOrders.AsNoTracking()
                                       on new { cost.TenantId, cost.WorkOrderId } equals new { work.TenantId, work.WorkOrderId }
                                   where cost.TenantId == tenant.TenantId && work.TargetUnitId == unitId
                                   select new InternalServiceCost("refurbishment", work.WorkOrderNumber, work.Status, cost.PostedAt,
                                       cost.Description, cost.Amount, cost.Currency)).ToListAsync(cancellationToken);
        return claims.Concat(rmas).Concat(refurbishment).OrderByDescending(row => row.RecordedAt).ToList();
    }

    private async Task<(List<Guid> Ids, string? NextCursor)> SelectionRootsAsync(
        Guid selectionId, string? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaximumPageRoots);
        var query = db.SelectionSetMembers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.SelectionId == selectionId);
        if (DecodeGuid(cursor) is { } after) query = query.Where(row => row.UnitId.CompareTo(after) > 0);
        var ids = await query.OrderBy(row => row.UnitId).Select(row => row.UnitId).Take(take + 1).ToListAsync(cancellationToken);
        if (ids.Count == 0 && cursor is null && !await db.SelectionSets.AsNoTracking()
                .AnyAsync(row => row.TenantId == tenant.TenantId && row.SelectionId == selectionId, cancellationToken))
            throw new CommandRefusedException("selection_unknown", $"Selection {selectionId} was not found.");
        var more = ids.Count > take;
        if (more) ids.RemoveAt(ids.Count - 1);
        return (ids, more ? EncodeGuid(ids[^1]) : null);
    }

    private async Task<List<TreeNode>> ExpandAsync(List<Guid> roots, CancellationToken cancellationToken)
    {
        var links = new List<(Guid Parent, Guid Child, string? Slot)>();
        var frontier = roots.Distinct().ToList();
        var seen = frontier.ToHashSet();
        for (var depth = 1; frontier.Count > 0; depth++)
        {
            if (depth > MaximumTreeDepth)
                throw new CommandRefusedException("report_tree_too_deep", $"The component tree exceeds {MaximumTreeDepth} levels.");
            var rows = await db.AssemblyLinks.AsNoTracking()
                .Where(row => row.TenantId == tenant.TenantId && frontier.Contains(row.ParentUnitId)
                    && row.ChildUnitId != null && row.RemovedAt == null)
                .OrderBy(row => row.ParentUnitId).ThenBy(row => row.Slot).ThenBy(row => row.ChildUnitId)
                .Select(row => new { Parent = row.ParentUnitId, Child = row.ChildUnitId!.Value, row.Slot })
                .Take(MaximumExpandedRowsPerPage + 1).ToListAsync(cancellationToken);
            if (rows.Count > MaximumExpandedRowsPerPage)
                throw new CommandRefusedException("report_page_too_large", $"One report page may expand to at most {MaximumExpandedRowsPerPage:N0} units.");
            links.AddRange(rows.Select(row => (row.Parent, row.Child, row.Slot)));
            if (roots.Count + links.Count > MaximumExpandedRowsPerPage)
                throw new CommandRefusedException("report_page_too_large", $"One report page may expand to at most {MaximumExpandedRowsPerPage:N0} units.");
            frontier = rows.Select(row => row.Child).Where(seen.Add).Distinct().ToList();
        }

        var children = links.GroupBy(row => row.Parent).ToDictionary(group => group.Key,
            group => group.OrderBy(row => row.Slot, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Child).ToList());
        var answer = new List<TreeNode>(roots.Count + links.Count);
        foreach (var root in roots)
        {
            var inRoot = new HashSet<Guid>();
            Walk(root, root, null, 0, null, inRoot);
        }
        return answer;

        void Walk(Guid root, Guid unit, Guid? parent, int depth, string? slot, HashSet<Guid> inRoot)
        {
            if (!inRoot.Add(unit)) return;
            answer.Add(new TreeNode(root, unit, parent, depth, slot));
            if (!children.TryGetValue(unit, out var childRows)) return;
            foreach (var child in childRows) Walk(root, child.Child, unit, depth + 1, child.Slot, inRoot);
        }
    }

    private static void EnsureExportCap(int count)
    {
        if (count > MaximumExportRows)
            throw new CommandRefusedException("export_too_large", $"An export may contain at most {MaximumExportRows:N0} rows; narrow the report.");
    }

    private static string ItemName(ItemLabel? label) => label is null ? "ITEM NOT FOUND"
        : label.Name is null ? label.Code : $"{label.Code} · {label.Name}";

    private static string PartyName(PartyLabel? label) => label?.Name ?? label?.Code ?? "PARTY NOT FOUND";

    private static string LocationName(LocationLabel? label) => label is null ? "LOCATION NOT FOUND"
        : label.Name is null ? label.Code : $"{label.Code} · {label.Name}";

    private static string? DocumentName(DocumentRef? document, IReadOnlyDictionary<DocumentKey, DocumentLabel> labels) =>
        document is null ? null : labels.GetValueOrDefault(DocumentKey.From(document))?.Number ?? "DOCUMENT NOT FOUND";

    private static string EncodeGuid(Guid value) => Convert.ToBase64String(value.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Guid? DecodeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 16) throw new FormatException();
            return new Guid(bytes);
        }
        catch (FormatException)
        {
            throw new CommandRefusedException("cursor_invalid", "The report cursor is invalid.");
        }
    }

    private static string EncodePair(Guid first, Guid second)
    {
        var bytes = new byte[32];
        first.TryWriteBytes(bytes);
        second.TryWriteBytes(bytes.AsSpan(16));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (Guid First, Guid Second)? DecodePair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 32) throw new FormatException();
            return (new Guid(bytes.AsSpan(0, 16)), new Guid(bytes.AsSpan(16, 16)));
        }
        catch (FormatException)
        {
            throw new CommandRefusedException("cursor_invalid", "The report cursor is invalid.");
        }
    }
}
