using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationReconstructionLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-history-migration-lead";

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Live_demo_extracts_profiles_canonicalises_and_reconstructs_verified_cancellations()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("reconlive");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var source = await sources.CreateAsync(new("Owner demo read-only history source", "live-history",
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

        var view = await reconstruction.GetAsync(batch.BatchId, default);
        Assert.NotNull(view);
        Assert.Equal(ReconstructionRunStatus.Completed, view.Status);
        foreach (var systemNumber in new[] { 221, 223 })
        {
            var sourceKey = $"S10000|{systemNumber}";
            var candidate = await db.MigrationUnitCandidates.AsNoTracking().SingleAsync(row =>
                row.RunId == view.CanonicalisationRunId && row.ItemCode == "S10000" &&
                row.PrimarySourceKey == sourceKey);
            var history = await reconstruction.HistoryAsync(candidate.CandidateId, null, 200, default);
            Assert.NotNull(history);
            Assert.DoesNotContain(history.Events, row => row.MovementKind == HistoryMovementKind.Shipped);
            Assert.Equal(2, history.LogEntries.Count(row =>
                row.ReasonCode == ReconstructionLogReason.CancelledInSource));
        }

        Console.WriteLine("RECON candidates={0} skipped={1} cancellations={2} took={3}",
            view.CandidatesReconstructed, view.CandidatesSkippedByDisposition,
            view.CancellationPairsRemoved, view.FinishedAt - view.StartedAt);
        foreach (var count in view.EventsByMovementKind)
            Console.WriteLine("MOVEMENT {0}: {1}", count.Key, count.Count);
        foreach (var count in view.HistoryDepth)
            Console.WriteLine("DEPTH {0}: {1}", count.Key, count.Count);
        foreach (var count in view.QueuedForReviewByReason)
            Console.WriteLine("REVIEW {0}: {1}", count.Key, count.Count);
        var unknowns = await db.MigrationHistoryEvents.AsNoTracking().Where(row =>
                row.ReconstructionRunId == view.RunId && row.MovementKind == HistoryMovementKind.Unknown)
            .GroupBy(row => row.SourceDocumentTypeLabel)
            .Select(group => new { Label = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Label).ToListAsync();
        foreach (var unknown in unknowns)
            Console.WriteLine("UNKNOWN {0}: {1}", unknown.Label, unknown.Count);
    }
}
