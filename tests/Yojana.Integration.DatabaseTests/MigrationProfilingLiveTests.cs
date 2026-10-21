using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Profiling;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationProfilingLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-migration-lead";

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Product_extraction_automatically_enqueues_and_completes_the_fixed_profile_pack()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("proflive");
        using var scope = fixture.ScopeFor(tenant);
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
        var source = await sources.CreateAsync(new("Owner demo read-only source", "live-profile",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, database,
            ConnectionString: connectionString), Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "OWNER-DEMO-READ-ONLY-2026-10-21",
                "Owner confirmed read-only extraction from the isolated demo company."), Actor, default);

        var started = await batches.StartAsync(new(source.SourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, started.Status);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var extracted = await batches.DetailAsync(started.BatchId, default);
        Assert.NotNull(extracted);
        Assert.Equal(MigrationBatchStatus.Succeeded, extracted.Status);

        var queued = await profiles.GetAsync(started.BatchId, default);
        Assert.NotNull(queued);
        Assert.Equal(MigrationProfileRunStatus.Queued, queued.Status);
        Assert.Equal(1, await profiles.RunPendingAsync(default));

        var profile = await profiles.GetAsync(started.BatchId, default);
        Assert.NotNull(profile);
        Assert.Contains(profile.Status,
            new[] { MigrationProfileRunStatus.Completed, MigrationProfileRunStatus.Blocked });
        Assert.NotNull(profile.StartedAt);
        Assert.NotNull(profile.FinishedAt);
        Assert.Equal(MigrationProfileClasses.All.Count, profile.Classes.Count);
        Assert.Equal(MigrationProfileClasses.All.Select(item => item.Code).Order(),
            profile.Classes.Select(item => item.Code).Order());

        foreach (var result in profile.Classes)
        {
            if (result.Code == MigrationProfileClasses.SerialsInUserFields.Code)
            {
                Assert.Null(result.Count);
                Assert.StartsWith("Not configured", result.Note);
            }
            else
            {
                Assert.NotNull(result.Count);
                Assert.True(result.Count >= 0);
            }

            Console.WriteLine("PROFILE {0}: count={1}; outcome={2}; note={3}", result.Code,
                result.Count?.ToString() ?? "null", result.Outcome, result.Note ?? "none");
            if (result.Count is not > 0) continue;

            var samples = await profiles.SamplesAsync(started.BatchId, result.Code, default);
            Assert.NotNull(samples);
            Assert.NotEmpty(samples.Samples);
            Assert.True(samples.Samples.Count <= 20);
            foreach (var sample in samples.Samples.Take(3))
                Console.WriteLine("SAMPLE {0}: {1}", result.Code, JsonSerializer.Serialize(sample));
        }
    }
}
