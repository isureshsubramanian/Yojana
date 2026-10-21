using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationCanonicalisationLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-migration-lead";

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Live_extraction_profiles_and_canonicalises_every_serial_once()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("canonlive");
        using var scope = fixture.ScopeFor(tenant);
        scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().Database
            .SetCommandTimeout(TimeSpan.FromMinutes(2));
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
        var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
        var source = await sources.CreateAsync(new("Owner demo read-only source", "live-canonical",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, database,
            ConnectionString: connectionString), Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "OWNER-DEMO-READ-ONLY-2026-10-21",
                "Owner confirmed read-only extraction from the isolated demo company."), Actor, default);

        var started = await batches.StartAsync(new(source.SourceId), Actor, default);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        Assert.Equal(1, await profiles.RunPendingAsync(default));
        Assert.False(await profiles.IsLoadBlocked(started.BatchId, default));
        await canonical.EnqueueEligibleAsync(default);
        Assert.Equal(1, await canonical.RunPendingAsync(default));

        var view = await canonical.GetAsync(started.BatchId, default);
        Assert.NotNull(view);
        Assert.Equal(CanonicalisationRunStatus.Completed, view.Status);
        Assert.True(view.CandidateRowsTotal > 0);
        Console.WriteLine("CANON candidate_rows={0} exact={1} trimmed_upper={2} family={3} waiting_items={4} " +
            "merged_rows={5} decided_items={6} carried_items={7} unverified={8} took={9}",
            view.CandidateRowsTotal, view.CandidateRowsExact, view.CandidateRowsTrimmedUpper,
            view.CandidateRowsFamilyNormalised, view.WaitingReviewItems, view.CandidateRowsMerged,
            view.DecidedReviewItems, view.CarriedOverReviewItems, view.UnverifiedDates,
            view.FinishedAt - view.StartedAt);
        foreach (var gap in view.Gaps) Console.WriteLine("GAP {0}", gap);

        var queue = await canonical.ListAsync(started.BatchId, null, null, null, null, 100, default);
        foreach (var kind in queue.Items.GroupBy(row => row.Kind))
            Console.WriteLine("QUEUE {0}: {1}", kind.Key, kind.Count());
    }
}
