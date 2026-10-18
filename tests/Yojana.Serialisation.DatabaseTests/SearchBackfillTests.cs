using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Search;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T25: the set-based backfill writes exactly what the per-entry indexer writes, in restartable batches.</summary>
[Collection("serialisation host")]
public sealed class SearchBackfillTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Backfill_matches_incremental_and_is_idempotent()
    {
        var tenant = await fixture.NewTenantAsync("bf");
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemCode = "BF-ITEM" });
        var marker = "BF" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var commissioned = await bus.InvokeAsync<CommissionUnitsResult>(new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
        [
            new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, marker + "-1")]),
            new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, marker + "-2")]),
        ], DateTime.UtcNow, "backfill", Guid.NewGuid(), LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse), new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
        var ids = commissioned.Lines.Select(l => l.UnitId!.Value).ToList();
        var entity = Guid.NewGuid();
        await bus.InvokeAsync<CorrectIdentifierResult>(new CorrectIdentifier(Guid.NewGuid(), ids[0], IdentifierType.InternalSerial, marker + "-1A", IdentifierCorrectionReason.Typo, DateTime.UtcNow.AddMinutes(1), "backfill", entity), new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
        await bus.InvokeAsync<OpenOwnershipResult>(new OpenOwnership(Guid.NewGuid(), ids[0], Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale, DateTime.UtcNow.AddMinutes(2), "backfill", entity), new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
        await bus.InvokeAsync<ChangeCustodyResult>(new ChangeCustody(Guid.NewGuid(), ids[0], LocationKind.CustomerSite, EpisodeReason.Sale, DateTime.UtcNow.AddMinutes(3), "backfill", entity, Guid.NewGuid(), Guid.NewGuid(), PartyType.Customer), new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var noItem = Guid.NewGuid();
        db.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = noItem, LifecycleStatus = LifecycleStatus.PendingIdentifier, Origin = UnitOrigin.Purchased,
            SerialisationPoint = SerialisationPoint.Receipt, RetentionClass = "standard_10y", CreatedAt = DateTime.UtcNow, CreatedEventId = Guid.NewGuid(), CreatedEventTime = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        ids.Add(noItem);
        var indexer = scope.ServiceProvider.GetRequiredService<SearchIndexer>();
        await indexer.ReindexUnitsAsync(ids, CancellationToken.None);
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db, tenant, [.. ids]);
        var backfill = scope.ServiceProvider.GetRequiredService<SearchBackfill>();
        await db.SearchTerms.Where(t => t.TenantId == tenant && t.EntityType == SearchEntityType.Unit && ids.Contains(t.EntityId)).ExecuteDeleteAsync();
        await db.SearchEntries.Where(e => e.TenantId == tenant && e.EntityType == SearchEntityType.Unit && ids.Contains(e.EntityId)).ExecuteDeleteAsync();
        await backfill.ReindexUnitsAsync(ids, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync(db, tenant, [.. ids]));
        var result = await backfill.BackfillUnitsAsync(CancellationToken.None);
        Assert.Equal(3, result.Units);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync(db, tenant, [.. ids]));
        // Again, in batches of two: three units take two batches on one connection, and the rows come out the same.
        var batched = await backfill.BackfillUnitsAsync(CancellationToken.None, batchSize: 2);
        Assert.Equal((3L, 2), (batched.Units, batched.Batches));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync(db, tenant, [.. ids]));
        var found = await scope.ServiceProvider.GetRequiredService<SearchQueries>().SearchAsync(marker + "-1A", null, SearchDisclosureTier.BackOffice, CancellationToken.None);
        Assert.Equal($"/units/{ids[0]}", found.OpenDirectly);
    }

    private static async Task<string[]> SnapshotAsync(SerialisationDbContext db, TenantId tenant, Guid[] ids)
    {
        var entries = await db.SearchEntries.AsNoTracking().Where(e => e.TenantId == tenant && ids.Contains(e.EntityId) && e.EntityType == SearchEntityType.Unit).ToListAsync();
        var terms = await db.SearchTerms.AsNoTracking().Where(t => t.TenantId == tenant && ids.Contains(t.EntityId) && t.EntityType == SearchEntityType.Unit).ToListAsync();
        return entries.Select(e => $"E|{e.EntityId}|{e.Title}|{e.TitleMono}|{e.Subtitle}|{e.SearchText}|{e.TypeWeight}|{e.Metadata}|{e.Href}")
            .Concat(terms.Select(t => $"T|{t.EntityId}|{t.Term}|{t.Kind}|{t.Detail}"))
            .Order(StringComparer.Ordinal).ToArray();
    }
}
