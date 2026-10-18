using Yojana.Bench;
using Yojana.Platform.Seeding;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class BenchOptionsTests
{
    private const string Connection = "Server=localhost,15433;Database=yojana_bench_test;User ID=sa;Password=Fictional1!;TrustServerCertificate=True";

    [Fact]
    public void Concurrency_defaults_to_the_development_tenant()
    {
        var options = BenchOptions.Parse(["concurrency", "--url", "http://localhost:5080", "--duration", "2m"], Environment);

        Assert.Null(options.TenantIndex);
        Assert.Equal(DevelopmentSeeder.DevTenantId.Value, options.ConcurrencyTenantId);
        Assert.Equal("DEV", options.ConcurrencyTenantLabel);
        Assert.Equal("admin@dev.yojana.local", options.Email);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Duration);
    }

    [Fact]
    public void Tenant_selects_the_benchmark_identity_and_default_administrator()
    {
        var options = BenchOptions.Parse(["concurrency", "--tenant", "3", "--url", "http://localhost:5080",
            "--engineers", "20", "--documents", "75"], Environment);

        Assert.Equal(3, options.TenantIndex);
        Assert.Equal(BenchOptions.TenantId(3), options.ConcurrencyTenantId);
        Assert.Equal("BENCH3", options.ConcurrencyTenantLabel);
        Assert.Equal("admin@bench3.yojana.local", options.Email);
        Assert.Equal(20, options.Engineers);
        Assert.Equal(75, options.Documents);
    }

    [Fact]
    public void Preparation_commands_require_a_positive_benchmark_tenant()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["prepare-concurrency"], Environment));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchOptions.Parse(["prepare-concurrency", "--tenant", "0"], Environment));
    }

    [Fact]
    public void Seed_users_requires_the_password_environment_variable()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["seed-users", "--tenant", "1"],
            name => name == "YOJANA_BENCH_SQL" ? Connection : null));
    }

    private static string? Environment(string name) => name switch
    {
        "YOJANA_BENCH_SQL" => Connection,
        "YOJANA_BENCH_PASSWORD" => "Fictional benchmark password",
        _ => null,
    };
}
