using Microsoft.Data.SqlClient;
using Xunit;
using Yojana.Bench;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationRehearsalTests(IntegrationHostFixture fixture)
{
    private const string PreScaleFiveThousandPlan =
        "901D45DA4C51CCC5B723D632C408A6571563ADF4EF1E326CD440116E4D70B30E";

    [DatabaseFact]
    public async Task Synthetic_source_is_deterministic_for_a_seed()
    {
        _ = fixture.Host;
        var source = SourceConnection("p9t36_hash");
        var first = await new MigrationSourceGenerator(source, 200, 36).GenerateAsync();
        var second = await new MigrationSourceGenerator(source, 200, 36).GenerateAsync();

        Assert.Equal(first.RowsWritten, second.RowsWritten);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [DatabaseFact]
    public async Task Rehearsal_loads_delta_and_both_undos_with_reconciled_counts()
    {
        _ = fixture.Host;
        var source = SourceConnection("p9t36_run");
        var generated = await new MigrationSourceGenerator(source, 200, 36).GenerateAsync();
        var output = Path.Combine(Path.GetTempPath(), "yojana-p9t36-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = BenchOptions.Parse([
                "migration-rehearsal", "--target", IntegrationHostFixture.ConnectionString!,
                "--source", source, "--tenant", "new", "--seed", "36", "--out", output,
            ], _ => null);
            var result = await new MigrationRehearsalRunner(options).RunAsync(CancellationToken.None);

            Assert.Equal(generated.RequestedSerials, result.SourceSerials);
            Assert.Equal(result.SourceSerials, result.Loaded + result.Refused + result.Excluded);
            Assert.True(result.DeltaNew > 0);
            Assert.True(result.DeltaCover > 0);
            Assert.True(result.DeltaOwner > 0);
            Assert.True(result.DeltaGone > 0);
            Assert.Equal(0, result.RemainingMigratedUnits);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [DatabaseFact]
    public async Task Optimised_mapping_matches_the_pre_scale_5000_serial_plan()
    {
        _ = fixture.Host;
        var source = SourceConnection("p9t36_mapping_equivalence");
        var generated = await new MigrationSourceGenerator(source, 5_000, 36).GenerateAsync();
        var output = Path.Combine(Path.GetTempPath(), "yojana-p9t36-equivalence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = BenchOptions.Parse([
                "migration-rehearsal", "--target", IntegrationHostFixture.ConnectionString!,
                "--source", source, "--tenant", "new", "--seed", "36", "--out", output,
            ], _ => null);
            var result = await new MigrationRehearsalRunner(options).RunAsync(CancellationToken.None);

            Assert.Equal(generated.RequestedSerials, result.SourceSerials);
            Assert.Equal(PreScaleFiveThousandPlan, result.PlanFingerprint);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    private static string SourceConnection(string suffix)
    {
        var connection = new SqlConnectionStringBuilder(IntegrationHostFixture.ConnectionString!);
        connection.InitialCatalog += "_" + suffix;
        connection.ApplicationName = "yojana-migration-rehearsal-tests";
        return connection.ConnectionString;
    }
}
