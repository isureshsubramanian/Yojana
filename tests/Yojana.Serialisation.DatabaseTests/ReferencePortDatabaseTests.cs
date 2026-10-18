using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class ReferencePortDatabaseTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Fallback_production_order_number_resolves_from_the_tenants_recorded_allocation()
    {
        var tenant = await fixture.NewTenantAsync("ref");
        var otherTenant = await fixture.NewTenantAsync("refx");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.SerialRanges.Add(new SerialRange
            {
                TenantId = tenant,
                RangeId = Guid.NewGuid(),
                Scheme = IdentifierScheme.Internal,
                ItemId = Guid.NewGuid(),
                StartValue = "REF-0001",
                EndValue = "REF-0001",
                StartOrdinal = 1,
                EndOrdinal = 1,
                Count = 1,
                AllocatedToDocumentType = order.Type,
                AllocatedToDocumentId = order.Id,
                ReservedAt = DateTime.UtcNow,
                ReservedBy = "reference-test",
                Status = SerialRangeStatus.Reserved,
            });
            await db.SaveChangesAsync();

            var port = scope.ServiceProvider.GetRequiredService<IDocumentNumberPort>();
            var fallback = ReferenceFallback.DocumentNumber(order);
            var result = Assert.Single(await port.FindAsync(order.Type, fallback, 10, CancellationToken.None));
            Assert.Equal(order.Id, result.Document.Id);
            // A partial number filters in SQL by the id's leading hex digits, typed with or without the prefix.
            Assert.Contains(await port.FindAsync(order.Type, fallback[..6], 10, CancellationToken.None), label => label.Document.Id == order.Id);
            Assert.Contains(await port.FindAsync(order.Type, fallback[3..].ToLowerInvariant(), 10, CancellationToken.None), label => label.Document.Id == order.Id);
            Assert.Empty(await port.FindAsync(order.Type, "MO-not-hex", 10, CancellationToken.None));
        }

        using var otherScope = fixture.ScopeFor(otherTenant);
        var otherPort = otherScope.ServiceProvider.GetRequiredService<IDocumentNumberPort>();
        Assert.Empty(await otherPort.FindAsync(order.Type, ReferenceFallback.DocumentNumber(order), 10, CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Fallback_lot_number_resolves_from_a_tenants_received_unit()
    {
        var tenant = await fixture.NewTenantAsync("lotref");
        var otherTenant = await fixture.NewTenantAsync("lotrefx");
        var lotId = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Units.Add(new Unit
            {
                TenantId = tenant,
                UnitId = Guid.NewGuid(),
                LifecycleStatus = LifecycleStatus.PreRelease,
                Origin = UnitOrigin.Purchased,
                LotId = lotId,
                SerialisationPoint = SerialisationPoint.Receipt,
                RetentionClass = "standard_10y",
                CreatedAt = At,
                CreatedEventId = Guid.NewGuid(),
                CreatedEventTime = At,
            });
            await db.SaveChangesAsync();

            var port = scope.ServiceProvider.GetRequiredService<IDocumentNumberPort>();
            var result = Assert.Single(await port.FindAsync("lot", ReferenceFallback.DocumentNumber(new DocumentRef("lot", lotId)), 10, CancellationToken.None));
            Assert.Equal(lotId, result.Document.Id);
            Assert.Equal($"LOT-{lotId:N}"[..12].ToUpperInvariant(), result.Number);
        }

        using var otherScope = fixture.ScopeFor(otherTenant);
        var otherPort = otherScope.ServiceProvider.GetRequiredService<IDocumentNumberPort>();
        Assert.Empty(await otherPort.FindAsync("lot", ReferenceFallback.DocumentNumber(new DocumentRef("lot", lotId)), 10, CancellationToken.None));
    }
}
