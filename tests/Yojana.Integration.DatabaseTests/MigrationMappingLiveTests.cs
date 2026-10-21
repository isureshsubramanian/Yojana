using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationMappingLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-mapping-lead";

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Live_demo_runs_the_complete_read_only_pipeline_into_a_mapping_plan()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("maplive");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var source = await sources.CreateAsync(new("Owner demo read-only mapping source", "live-mapping",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, database,
            ConnectionString: connectionString), Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "OWNER-DEMO-READ-ONLY-2026-10-22",
                "Owner confirmed read-only extraction from the isolated demo company."), Actor, default);

        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var batch = await batches.StartAsync(new(source.SourceId), Actor, default);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
        Assert.Equal(1, await profiles.RunPendingAsync(default));
        Assert.False(await profiles.IsLoadBlocked(batch.BatchId, default));
        var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
        Assert.Equal(1, await canonical.EnqueueEligibleAsync(default));
        Assert.Equal(1, await canonical.RunPendingAsync(default));
        var reconstruction = scope.ServiceProvider.GetRequiredService<ReconstructionService>();
        Assert.Equal(1, await reconstruction.EnqueueEligibleAsync(default));
        Assert.Equal(1, await reconstruction.RunPendingAsync(default));

        var waiting = await canonical.ListAsync(batch.BatchId, MigrationReviewKind.HistoryNeedsReview,
            null, MigrationReviewStatus.Waiting, null, 100, default);
        var issued = waiting.Items.Where(row => row.CandidateSummary.Contains("issued out",
            StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, issued.Count);
        foreach (var item in issued)
            await canonical.DecideAsync(item.ItemId, new(MigrationReviewDecisionKind.AcceptAsRecorded, null,
                "The issued-out source movement was checked and accepted as recorded."), Actor, default);
        Assert.Empty((await canonical.ListAsync(batch.BatchId, null, null, MigrationReviewStatus.Waiting,
            null, 100, default)).Items);

        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();
        Assert.Equal(1, await mapping.EnqueueEligibleAsync(default));
        Assert.Equal(1, await mapping.RunPendingAsync(default));
        var view = await mapping.GetAsync(batch.BatchId, default);
        Assert.NotNull(view);
        Assert.Equal(MappingRunStatus.Completed, view.Status);
        Assert.True(view.UnitsByOrigin.Sum(row => row.Count) > 0);

        Console.WriteLine("MAP run={0} cutover={1} status={2} excluded={3} waiting_left_out={4} replacements={5} expired={6} took={7}",
            view.RunId, view.CutoverDate, view.Status, view.ExcludedCandidates,
            view.WaitingCandidatesLeftOut, view.ReplacementLinks, view.ExpiredEntitlements,
            view.FinishedAt - view.StartedAt);
        Print("UNITS_ORIGIN", view.UnitsByOrigin);
        Print("UNITS_LOCATION", view.UnitsByLocationState);
        Print("IDENTIFIERS_ISSUER", view.IdentifiersByIssuer);
        Print("EVENTS_PROVENANCE", view.EventsByProvenance);
        Print("OWNERSHIP", view.Ownership);
        Print("ENTITLEMENTS_FAMILY", view.EntitlementsByFamily);
        Print("ENTITLEMENTS_DATE_SOURCE", view.EntitlementsByDateSource);
        Print("GAPS", view.GapsByKind);
        var storedGaps = await db.MigrationPlanGaps.AsNoTracking().Where(row => row.MapRunId == view.RunId)
            .GroupBy(row => row.Kind).Select(group => new { Kind = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Kind).ToListAsync();
        foreach (var gap in storedGaps) Console.WriteLine("GAP {0}: {1}", gap.Kind, gap.Count);
    }

    private static void Print(string heading, IReadOnlyList<MappingCount> counts)
    {
        foreach (var count in counts) Console.WriteLine("{0} {1}: {2}", heading, count.Key, count.Count);
    }
}
