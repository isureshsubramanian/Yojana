using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Gateway;

public sealed class IntegrationBomQueryPort(IntegrationDbContext db, ITenantContext tenant) : IBomQueryPort
{
    public async Task<BillOfMaterials?> BillOfMaterialsAsync(Guid itemId,
        CancellationToken cancellationToken) =>
        (await ReadAsync([itemId], cancellationToken)).GetValueOrDefault(itemId);

    public async Task<IReadOnlyList<ExpectedComponent>> ExpectedSerialisedComponentsAsync(Guid itemId,
        DateOnly effectiveAt, CancellationToken cancellationToken) =>
        Expected((await ReadAsync([itemId], cancellationToken)).GetValueOrDefault(itemId));

    public async Task<IReadOnlyDictionary<ExpectedComponentQuery, IReadOnlyList<ExpectedComponent>>>
        ExpectedSerialisedComponentsAsync(IReadOnlyCollection<ExpectedComponentQuery> queries,
            CancellationToken cancellationToken)
    {
        var distinct = queries.Distinct().ToList();
        var bills = await ReadAsync(distinct.Select(query => query.ItemId).Distinct().ToList(), cancellationToken);
        return distinct.ToDictionary(query => query,
            query => (IReadOnlyList<ExpectedComponent>)Expected(bills.GetValueOrDefault(query.ItemId)));
    }

    private async Task<IReadOnlyDictionary<Guid, BillOfMaterials>> ReadAsync(List<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0) return new Dictionary<Guid, BillOfMaterials>();
        // Project to a mutable read row before filtering/grouping: positional records constructed inside an EF query
        // are not translated by SQL Server's provider.
        var rows = await (from header in db.BomProjections.AsNoTracking()
                where header.TenantId == tenant.TenantId && itemIds.Contains(header.ParentItemId) && !header.Deleted
                from line in db.BomProjectionLines.AsNoTracking()
                    .Where(value => value.TenantId == header.TenantId
                        && value.BomProjectionId == header.BomProjectionId).DefaultIfEmpty()
                from item in db.ReferenceProjections.AsNoTracking()
                    .Where(value => line != null && line.ItemId != null && value.TenantId == header.TenantId
                        && value.EntityType == "item" && value.ModuleId == line.ItemId && !value.Deleted)
                    .DefaultIfEmpty()
                select new BomReadRow
                {
                    ParentItemId = header.ParentItemId,
                    SourceChangedAt = header.SourceChangedAt,
                    LineNo = line == null ? null : line.LineNo,
                    Kind = line == null ? null : line.Kind,
                    ItemId = line == null ? null : line.ItemId,
                    ItemCode = line == null ? null : line.ItemExternalId,
                    Description = line == null ? null : line.Description,
                    Quantity = line == null ? null : line.Quantity,
                    Uom = line == null ? null : line.Uom,
                    TrackingMode = item == null ? null : item.TrackingMode,
                    ItemAttributesJson = item == null ? null : item.AttributesJson,
                }).ToListAsync(cancellationToken);

        return rows.GroupBy(row => new { row.ParentItemId, row.SourceChangedAt }).ToDictionary(
            group => group.Key.ParentItemId,
            group => new BillOfMaterials(group.Key.SourceChangedAt,
                group.Where(row => row.LineNo is not null).OrderBy(row => row.LineNo).Select(Line).ToList()));
    }

    private static BillOfMaterialsLine Line(BomReadRow row)
    {
        bool? inventory = null;
        if (row.ItemAttributesJson is not null)
        {
            var attributes = JsonSerializer.Deserialize<ItemReferenceAttributes>(row.ItemAttributesJson,
                IntegrationContracts.Json);
            inventory = attributes?.IsInventoryItem;
        }
        return new(row.LineNo!.Value, row.Kind!, row.ItemId, row.ItemCode, row.Description ?? "",
            row.Quantity ?? 0, row.Uom,
            row.TrackingMode is "serial" or "serial_and_lot", inventory);
    }

    private static List<ExpectedComponent> Expected(BillOfMaterials? billOfMaterials) =>
        billOfMaterials?.Lines.Where(line => line.Kind == "item" && line.ItemId is not null
                && line.Serialised && line.InventoryItem != false && line.Quantity > 0)
            .Select(line => new ExpectedComponent(line.LineNo.ToString(CultureInfo.InvariantCulture),
                line.ItemId!.Value, line.ItemCode ?? line.ItemId.Value.ToString("N"),
                decimal.ToInt32(decimal.Ceiling(line.Quantity))))
            .ToList() ?? [];

    private sealed class BomReadRow
    {
        public Guid ParentItemId { get; init; }
        public DateTimeOffset SourceChangedAt { get; init; }
        public int? LineNo { get; init; }
        public string? Kind { get; init; }
        public Guid? ItemId { get; init; }
        public string? ItemCode { get; init; }
        public string? Description { get; init; }
        public decimal? Quantity { get; init; }
        public string? Uom { get; init; }
        public string? TrackingMode { get; init; }
        public string? ItemAttributesJson { get; init; }
    }
}

public sealed class ErpBomCapabilityPort(IntegrationDbContext db, ITenantContext tenant) : IErpBomCapabilityPort
{
    public Task<bool> HasActiveBomSourceAsync(CancellationToken cancellationToken) =>
        db.Connections.AsNoTracking().AnyAsync(connection => connection.TenantId == tenant.TenantId
            && connection.Status == ConnectionStatus.Active
            && db.ConnectionCapabilities.Any(capability => capability.TenantId == connection.TenantId
                && capability.ConnectionId == connection.ConnectionId
                && capability.CanonicalMessage == nameof(BomUpserted)
                && capability.SupportLevel == CapabilitySupport.Native), cancellationToken);
}
