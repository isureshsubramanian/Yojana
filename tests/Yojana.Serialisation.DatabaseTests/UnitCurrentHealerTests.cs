using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Scheduling;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Projections;

namespace Yojana.Serialisation.DatabaseTests;

#pragma warning disable EF1002 // schema constants and test-generated ids only

/// <summary>P1-T05: the healer finds and repairs divergence between the ledger and <c>unit_current</c>.</summary>
[Collection("serialisation host")]
public sealed class UnitCurrentHealerTests(SerialisationHostFixture fixture)
{
    private const string S = SerialisationDbContext.SchemaName;

    [DatabaseFact]
    public async Task Divergent_and_missing_rows_are_reported_and_repaired_from_the_ledger()
    {
        var tenant = await fixture.NewTenantAsync("hl");
        var good = TimeOrderedId.New();
        var tampered = TimeOrderedId.New();
        var missing = TimeOrderedId.New();
        var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
        var location = Guid.NewGuid();
        var entitlement = Guid.NewGuid();
        var warrantyEnd = new DateOnly(2028, 10, 6);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            foreach (var unit in new[] { good, tampered, missing })
            {
                await writer.AppendAsync(
                [
                    new UnitEventDraft { UnitId = unit, EventTime = at, EventType = UnitEventType.Commissioned, LegalEntityId = Guid.NewGuid(), ActorRef = "test", IdempotencyKey = IdempotencyKey.ForCommand("t", Guid.NewGuid(), unit, UnitEventType.Commissioned, at), Delta = new CurrentStateDelta { Create = new CreateCurrentState(Guid.NewGuid(), LifecycleStatus.PreRelease, UnitDisposition.InProgress) } },
                    new UnitEventDraft { UnitId = unit, EventTime = at.AddHours(1), EventType = UnitEventType.Released, LegalEntityId = Guid.NewGuid(), ActorRef = "test", IdempotencyKey = IdempotencyKey.ForCommand("t", Guid.NewGuid(), unit, UnitEventType.Released, at.AddHours(1)), Delta = new CurrentStateDelta { LifecycleStatus = LifecycleStatus.Released, Disposition = UnitDisposition.Available, LocationId = location, LocationKind = LocationKind.Warehouse, LiveEntitlementIds = [entitlement], WarrantyEndMax = new Optional<DateOnly?>(warrantyEnd) } },
                ], CancellationToken.None);
            }

            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await db.SaveChangesAsync();
            // Simulate divergence the way it would really happen: a direct write that bypassed the writer.
            await db.Database.ExecuteSqlRawAsync($"UPDATE [{S}].[unit_current] SET lifecycle_status = 'scrapped', location_id = NULL, live_entitlement_ids = '[]', warranty_end_max = NULL WHERE unit_id = '{tampered}'");
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM [{S}].[unit_current] WHERE unit_id = '{missing}'");
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var healer = scope.ServiceProvider.GetRequiredService<UnitCurrentHealer>();
            var dryRun = await healer.RunAsync(repair: false, pageSize: 2, CancellationToken.None);
            Assert.Equal(3, dryRun.UnitsChecked);
            Assert.Equal(0, dryRun.Repaired);
            var t = Assert.Single(dryRun.Divergent, d => d.UnitId == tampered);
            Assert.Equal(["lifecycle_status", "location_id", "live_entitlement_ids", "warranty_end_max"], t.Fields);
            var m = Assert.Single(dryRun.Divergent, d => d.UnitId == missing);
            Assert.True(m.RowMissing);
            Assert.DoesNotContain(dryRun.Divergent, d => d.UnitId == good);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var report = await scope.ServiceProvider.GetRequiredService<UnitCurrentHealer>().RunAsync(repair: true, pageSize: 2, CancellationToken.None);
            Assert.Equal(2, report.Repaired);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            foreach (var unit in new[] { good, tampered, missing })
            {
                var row = await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == unit);
                Assert.Equal(LifecycleStatus.Released, row.LifecycleStatus);
                Assert.Equal(location, row.LocationId);
                Assert.Contains(entitlement.ToString(), row.LiveEntitlementIds, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(warrantyEnd, row.WarrantyEndMax);
                Assert.Equal(await db.Events.Where(e => e.UnitId == unit).MaxAsync(e => e.Seq), row.LastEventSeq);
            }

            var clean = await scope.ServiceProvider.GetRequiredService<UnitCurrentHealer>().RunAsync(repair: false, pageSize: 2, CancellationToken.None);
            Assert.Empty(clean.Divergent);
        }
    }

    [DatabaseFact]
    public async Task The_healer_is_a_recurring_tenant_job()
    {
        var tenant = await fixture.NewTenantAsync("hj");
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await fixture.Services.GetRequiredService<RecurringTenantJobScheduler>().EnsureScheduledAsync(scheduler, [tenant], CancellationToken.None);
        var detail = await scheduler.GetJobDetail(TenantJob.KeyFor(UnitCurrentHealerJob.JobName, tenant));
        Assert.NotNull(detail);
        Assert.Equal(typeof(UnitCurrentHealerJob), detail.JobType);
    }
}
