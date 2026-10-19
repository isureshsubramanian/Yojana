using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Gateway;

public sealed record ItemReferenceAttributes(bool IsWarrantyProduct, string? WarrantyTemplateRef);

public sealed class GatewayReferenceStore(IntegrationDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<Guid> ResolveAsync(Guid connectionId, string entityType, string externalId,
        bool provisional, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = db.ExternalIdMaps.Local.FirstOrDefault(x => x.TenantId == tenantId
            && x.ConnectionId == connectionId && x.EntityType == entityType && x.ExternalId == externalId)
            ?? await db.ExternalIdMaps.SingleOrDefaultAsync(x => x.TenantId == tenantId
            && x.ConnectionId == connectionId && x.EntityType == entityType && x.ExternalId == externalId
            && x.ValidTo == null,
            cancellationToken);
        if (row is not null) return row.ModuleId;
        var now = clock.UtcNow;
        row = new ExternalIdMap
        {
            TenantId = tenantId, ExternalIdMapId = TimeOrderedId.New(now), ConnectionId = connectionId,
            EntityType = entityType, ExternalId = externalId, ModuleId = TimeOrderedId.New(now),
            ValidFrom = now, MappingSource = ExternalIdMappingSource.Auto,
            NaturalKeyHash = NaturalKey(entityType, externalId),
            MappedBy = $"system:connection:{connectionId:N}", MappedAt = now,
        };
        db.ExternalIdMaps.Add(row);
        return row.ModuleId;
    }

    public async Task<Guid> UpsertItemAsync(Guid connectionId, ItemUpserted item,
        CancellationToken cancellationToken)
    {
        var id = await ResolveAsync(connectionId, "item", item.ItemExternalId, false, cancellationToken);
        var row = db.ReferenceProjections.Local.SingleOrDefault(x => x.TenantId == tenant.TenantId
            && x.EntityType == "item" && x.ModuleId == id)
            ?? await db.ReferenceProjections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntityType == "item" && x.ModuleId == id,
            cancellationToken);
        if (row is null)
        {
            row = new ReferenceProjection
            {
                TenantId = tenant.TenantId, EntityType = "item", ModuleId = id,
                AttributesJson = "{}", UpdatedAt = clock.UtcNow,
            };
            db.ReferenceProjections.Add(row);
        }
        row.Code = item.ItemExternalId;
        row.DisplayName = item.Description;
        row.TrackingMode = item.TrackingMode.RawValue ?? item.TrackingMode.Value;
        row.Uom = item.Uom;
        row.AttributesJson = JsonSerializer.Serialize(
            new ItemReferenceAttributes(item.IsWarrantyProduct, item.WarrantyTemplateRef), IntegrationContracts.Json);
        row.Deleted = item.Deleted;
        row.UpdatedAt = clock.UtcNow;
        return id;
    }

    public async Task<Guid> UpsertPartyAsync(Guid connectionId, PartyUpserted party,
        CancellationToken cancellationToken)
    {
        var id = await ResolveAsync(connectionId, "party", party.PartyExternalId, false, cancellationToken);
        var row = db.ReferenceProjections.Local.SingleOrDefault(x => x.TenantId == tenant.TenantId
            && x.EntityType == "party" && x.ModuleId == id)
            ?? await db.ReferenceProjections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntityType == "party" && x.ModuleId == id,
            cancellationToken);
        if (row is null)
        {
            row = new ReferenceProjection
            {
                TenantId = tenant.TenantId, EntityType = "party", ModuleId = id,
                AttributesJson = "{}", UpdatedAt = clock.UtcNow,
            };
            db.ReferenceProjections.Add(row);
        }
        row.Code = party.PartyExternalId;
        row.DisplayName = party.LegalName;
        row.Deleted = party.Deleted;
        row.UpdatedAt = clock.UtcNow;
        return id;
    }

    public async Task<Guid?> FindAsync(Guid connectionId, string entityType, string externalId,
        CancellationToken cancellationToken) => await db.ExternalIdMaps.AsNoTracking()
        .Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.EntityType == entityType && x.ExternalId == externalId && x.ValidTo == null)
        .Select(x => (Guid?)x.ModuleId).SingleOrDefaultAsync(cancellationToken);

    private static string NaturalKey(string entityType, string externalId) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{entityType.Trim().ToUpperInvariant()}\n{externalId.Trim().ToUpperInvariant()}"))).ToLowerInvariant();
}

public sealed class IntegrationItemQueryPort(IntegrationDbContext db, ITenantContext tenant) : IItemQueryPort
{
    public async Task<ItemSerialisationProfile?> GetAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var row = await db.ReferenceProjections.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntityType == "item" && x.ModuleId == itemId && !x.Deleted, cancellationToken);
        return row is null ? null : Profile(row);
    }

    public async Task<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>> GetManyAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var rows = await db.ReferenceProjections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.EntityType == "item" && itemIds.Contains(x.ModuleId) && !x.Deleted).ToListAsync(cancellationToken);
        var byId = rows.GroupBy(x => x.ModuleId).ToDictionary(x => x.Key, x => Profile(x.First()));
        return itemIds.Distinct().ToDictionary(id => id,
            id => byId.TryGetValue(id, out var profile) ? profile : null);
    }

    private static ItemSerialisationProfile Profile(ReferenceProjection row) => new(row.ModuleId,
        row.Code ?? row.ModuleId.ToString("N"), row.TrackingMode == "serial_and_lot" ? ItemSerialisationProfile.TrackingModes.Serial
            : row.TrackingMode ?? ItemSerialisationProfile.TrackingModes.Serial,
        SerialisationPoint.ProductionRelease, "standard_10y", ItemSerialisationProfile.ReusePolicies.Never,
        ItemSerialisationProfile.Completeness.Block, IdentifierScheme.Internal);
}
