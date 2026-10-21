using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Staging;

namespace Yojana.Integration.DatabaseTests;

internal sealed class MigrationLiveFactAttribute : FactAttribute
{
    public MigrationLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOJANA_TEST_SQL")))
            Skip = "YOJANA_MIGRATION_LIVE_SQL and YOJANA_TEST_SQL are required.";
    }
}

[Collection("integration host")]
public sealed class MigrationSourceLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-migration-admin";

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Product_extractor_resolves_the_live_schema_and_replays_unchanged()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("miglive");
        using var scope = fixture.ScopeFor(tenant);
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var source = await sources.CreateAsync(new("Owner demo read-only source", "live-schema",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, database,
            ConnectionString: connectionString), Actor, default);
        source = await sources.SetLicenceAsync(source.SourceId,
            new(true, "OWNER-DEMO-READ-ONLY-2026-10-21",
                "Owner confirmed read-only extraction from the isolated demo company."), Actor, default);
        Assert.Equal(MigrationLicencePosition.Confirmed, source.LicencePosition);

        var tested = await sources.TestAsync(source.SourceId, default);
        Assert.Equal(SourceProtocol.Tables.Count, tested.Tables.Count);
        Assert.All(tested.Tables, table =>
        {
            Assert.Null(table.Error);
            Assert.NotNull(table.RowCount);
        });

        var first = await RunAsync(batches, source.SourceId);
        var second = await RunAsync(batches, source.SourceId);
        foreach (var testedTable in tested.Tables.OrderBy(table => table.Table, StringComparer.Ordinal))
        {
            var firstTable = Assert.Single(first.Tables!, table => table.Table == testedTable.Table);
            var secondTable = Assert.Single(second.Tables!, table => table.Table == testedTable.Table);
            Assert.Equal(testedTable.RowCount, firstTable.RowsRead);
            Assert.Equal(firstTable.RowsRead, firstTable.Inserted);
            Assert.Equal(0, firstTable.Updated + firstTable.Unchanged + firstTable.Absent);
            Assert.Equal(testedTable.RowCount, secondTable.RowsRead);
            Assert.Equal(secondTable.RowsRead, secondTable.Unchanged);
            Assert.Equal(0, secondTable.Inserted + secondTable.Updated + secondTable.Absent);
            Console.WriteLine("{0}: test={1}; first rows={2}, inserted={3}; second rows={4}, unchanged={5}",
                testedTable.Table, testedTable.RowCount, firstTable.RowsRead, firstTable.Inserted,
                secondTable.RowsRead, secondTable.Unchanged);
        }
    }

    private static async Task<MigrationBatchView> RunAsync(MigrationBatchService batches, Guid sourceId)
    {
        var started = await batches.StartAsync(new(sourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, started.Status);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var result = await batches.DetailAsync(started.BatchId, default);
        Assert.NotNull(result);
        Assert.Equal(MigrationBatchStatus.Succeeded, result.Status);
        return result;
    }
}
