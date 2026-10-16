using System.Globalization;

namespace Yojana.Bench;

public sealed class BenchOptions
{
    public string Command { get; private set; } = "all";
    public string Server { get; private set; } = "";
    public string Database { get; private set; } = "yojana_bench";
    public string ConnectionString { get; private set; } = "";
    public int Units { get; private set; } = 1_000_000;
    public long Events { get; private set; } = 150_000_000;
    public int Tenants { get; private set; } = 4;
    public int SmallUnits { get; private set; } = 20_000;
    public long SmallEvents { get; private set; } = 1_500_000;
    public int Samples { get; private set; } = 1_000;
    public int Partitions { get; private set; } = 180;
    public bool SkipLifecycle { get; private set; }
    public string OutDirectory { get; private set; } = "docs/plan/benchmarks";

    /// <summary>The main tenant carries the full scale; the others exist so row-level security has something to filter.</summary>
    public static Guid TenantId(int index) => Guid.ParseExact($"b0000000-0000-4000-8000-{index:D12}", "D");

    public static readonly Guid LegalEntity = Guid.ParseExact("00000000-0000-0000-0000-000000000001", "D");

    public static BenchOptions Parse(string[] args)
    {
        var o = new BenchOptions();
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)) o.Command = args[0];
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--units": o.Units = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--events": o.Events = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--tenants": o.Tenants = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--small-units": o.SmallUnits = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--small-events": o.SmallEvents = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--samples": o.Samples = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--partitions": o.Partitions = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--database": o.Database = Next(); break;
                case "--out": o.OutDirectory = Next(); break;
                case "--skip-lifecycle": o.SkipLifecycle = true; break;
            }
        }

        var full = Environment.GetEnvironmentVariable("YOJANA_BENCH_SQL");
        if (!string.IsNullOrEmpty(full))
        {
            o.ConnectionString = full;
            o.Server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(full).DataSource;
        }
        else
        {
            o.Server = Environment.GetEnvironmentVariable("YOJANA_BENCH_SQL_HOST") ?? throw new InvalidOperationException("YOJANA_BENCH_SQL_HOST (or YOJANA_BENCH_SQL) is required");
            var password = Environment.GetEnvironmentVariable("YOJANA_BENCH_SQL_PASSWORD") ?? throw new InvalidOperationException("YOJANA_BENCH_SQL_PASSWORD is required");
            o.ConnectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = o.Server, InitialCatalog = o.Database, UserID = "sa", Password = password, Encrypt = true, TrustServerCertificate = true,
                ConnectTimeout = 60, MaxPoolSize = 200, ApplicationName = "yojana-bench",
            }.ConnectionString;
        }

        return o;
    }
}
