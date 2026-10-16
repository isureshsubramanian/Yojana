using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.DatabaseTests;

#pragma warning disable EF1002 // schema constants only

/// <summary>P1-T03: split ahead, switch cold partitions to the archive, merge old boundaries; nothing is deleted.</summary>
[Collection("serialisation host")]
public sealed class PartitionLifecycleTests(SerialisationHostFixture fixture)
{
    private const string S = SerialisationDbContext.SchemaName;

    private PartitionLifecycleService ServiceFor(PartitionLifecycleOptions options) =>
        new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<PartitionLifecycleService>.Instance);

    [DatabaseFact]
    public async Task The_job_is_scheduled_once_for_the_database()
    {
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var detail = await scheduler.GetJobDetail(PartitionLifecycleJob.Key);
        Assert.NotNull(detail);
        var triggers = await scheduler.GetTriggersOfJob(PartitionLifecycleJob.Key);
        Assert.Single(triggers);
        Assert.NotNull(triggers[0].NextFireTimeUtc);
    }

    [DatabaseFact]
    public async Task Boundaries_are_split_twelve_months_ahead_and_the_run_is_idempotent()
    {
        var service = ServiceFor(new PartitionLifecycleOptions { MonthsAhead = 12, ArchiveAfterMonths = 600, MergeAfterMonths = null });
        var before = await service.BoundariesAsync(CancellationToken.None);
        var today = before[^1].AddMonths(-6); // six months inside the existing range: six boundaries are missing
        var first = await service.RunAsync(today, CancellationToken.None);
        var after = await service.BoundariesAsync(CancellationToken.None);
        Assert.Equal(6, first.BoundariesAdded.Count);
        Assert.Equal(before[^1].AddMonths(6), after[^1]);
        Assert.True(IsMonthlyContiguous(after));
        var second = await service.RunAsync(today, CancellationToken.None);
        Assert.Empty(second.BoundariesAdded);
        Assert.Empty(second.PartitionsArchived);
    }

    [DatabaseFact]
    public async Task Cold_partitions_switch_into_the_archive_and_old_boundaries_merge_without_losing_rows()
    {
        var tenant = await fixture.NewTenantAsync("p");
        var unit = TimeOrderedId.New();
        var cold = new DateTime(2024, 2, 10, 9, 0, 0, DateTimeKind.Utc);   // partition [2024-02, 2024-03)
        var older = new DateTime(2024, 1, 20, 9, 0, 0, DateTimeKind.Utc);  // partition [2024-01, 2024-02): its boundary will be merged
        var warm = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Units.Add(new Unit { TenantId = tenant, UnitId = unit, LifecycleStatus = LifecycleStatus.Released, Origin = UnitOrigin.Migrated, SerialisationPoint = SerialisationPoint.Receipt, RetentionClass = "standard_10y", CreatedAt = DateTime.UtcNow, CreatedEventId = TimeOrderedId.New() });
            foreach (var (at, n) in new[] { (cold, 1), (older, 2), (warm, 3) })
            {
                db.Events.Add(new UnitEvent { TenantId = tenant, EventTime = at, EventId = TimeOrderedId.New(at), UnitId = unit, Seq = n, EventType = UnitEventType.Commissioned, LegalEntityId = Guid.NewGuid(), ActorRef = "test", RecordedTime = DateTime.UtcNow, IdempotencyKey = "pl-" + Guid.NewGuid().ToString("N") });
            }

            await db.SaveChangesAsync();
        }

        // Today = 2027-06: archive months whose upper boundary is at or before 2024-06 (36 months); merge boundaries before 2024-02 (40 months).
        // On a reused database February was archived by an earlier run, so this run exercises the staged path.
        {
            var service = ServiceFor(new PartitionLifecycleOptions { MonthsAhead = 12, ArchiveAfterMonths = 36, MergeAfterMonths = 40 });
            var result = await service.RunAsync(new DateOnly(2027, 6, 15), CancellationToken.None);
            Assert.Contains(new DateOnly(2024, 2, 1), result.PartitionsArchived);
            var after = await service.PartitionsAsync(CancellationToken.None);
            foreach (var coldPartition in after.Where(p => p.LowerBoundary is null || p.LowerBoundary < new DateOnly(2024, 6, 1)))
            {
                Assert.Equal(0, coldPartition.LiveRows);
                Assert.Equal(0, coldPartition.StageRows);
            }

            Assert.True(after.Single(p => p.LowerBoundary == new DateOnly(2024, 2, 1)).ArchiveRows > 0);
            var boundaries = await service.BoundariesAsync(CancellationToken.None);
            Assert.DoesNotContain(new DateOnly(2024, 1, 1), boundaries);
            Assert.True(IsMonthlyContiguous(boundaries));
            Assert.True(boundaries[^1] >= new DateOnly(2028, 6, 1));
        }

        // The tenant still sees all three events, split between the live table and the archive; nothing was lost.
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(1, await db.Events.CountAsync(e => e.UnitId == unit));
            Assert.Equal(2, await db.ArchivedEvents.CountAsync(e => e.UnitId == unit));
            Assert.Equal(0, await db.ArchiveStage.CountAsync());
            var januaryPartition = await db.Database.SqlQueryRaw<int>($"SELECT MAX($PARTITION.{SerialisationDbContext.PartitionFunction}(event_time)) AS [Value] FROM (SELECT event_time FROM [{S}].[unit_event_archive] WHERE unit_id = '{unit}' UNION ALL SELECT event_time FROM [{S}].[unit_event] WHERE unit_id = '{unit}') x WHERE event_time < '2024-02-01'").SingleAsync();
            Assert.Equal(1, januaryPartition); // the 2024-01 boundary is gone, so January rows sit in the catch-all partition
        }
    }

    private static bool IsMonthlyContiguous(IReadOnlyList<DateOnly> boundaries)
    {
        for (var i = 1; i < boundaries.Count; i++)
        {
            if (boundaries[i] != boundaries[i - 1].AddMonths(1))
            {
                return false;
            }
        }

        return boundaries.Count > 0 && boundaries.All(b => b.Day == 1);
    }
}
