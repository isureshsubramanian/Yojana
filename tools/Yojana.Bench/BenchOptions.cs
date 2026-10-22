using System.Globalization;
using Yojana.Platform.Seeding;

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
    public Uri? Url { get; private set; }
    public int Engineers { get; private set; } = 120;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(10);
    public int ContentionPercent { get; private set; } = 15;
    public int? TenantIndex { get; private set; }
    public int Documents { get; private set; } = 50;
    public string NumberingMode { get; private set; } = "sequence";
    public string Email { get; private set; } = "admin@dev.yojana.local";
    public string Password { get; private set; } = "";
    public string? Weights { get; private set; }
    public int Serials { get; private set; } = 20_000;
    public int Seed { get; private set; } = 36;
    public string? SourceConnectionString { get; private set; }
    public string? TargetConnectionString { get; private set; }
    public string? TenantSelection { get; private set; }
    public bool CapturePlans { get; private set; }

    public Guid ConcurrencyTenantId => TenantIndex is { } index ? TenantId(index) : DevelopmentSeeder.DevTenantId.Value;
    public string ConcurrencyTenantLabel => TenantIndex is { } index ? $"BENCH{index}" : DevelopmentSeeder.DevTenantCode;

    /// <summary>The main tenant carries the full scale; the others exist so row-level security has something to filter.</summary>
    public static Guid TenantId(int index) => Guid.ParseExact($"b0000000-0000-4000-8000-{index:D12}", "D");

    public static readonly Guid LegalEntity = Guid.ParseExact("00000000-0000-0000-0000-000000000001", "D");

    public static BenchOptions Parse(string[] args) => Parse(args, Environment.GetEnvironmentVariable);

    public static BenchOptions Parse(string[] args, Func<string, string?> environment)
    {
        var environmentPassword = environment("YOJANA_BENCH_PASSWORD") ?? "";
        var o = new BenchOptions { Password = environmentPassword };
        var emailSpecified = false;
        var outputSpecified = false;
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
                case "--out": o.OutDirectory = Next(); outputSpecified = true; break;
                case "--skip-lifecycle": o.SkipLifecycle = true; break;
                case "--url": o.Url = new Uri(Next(), UriKind.Absolute); break;
                case "--engineers": o.Engineers = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--duration": o.Duration = ParseDuration(Next()); break;
                case "--contention": o.ContentionPercent = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--tenant":
                {
                    var tenant = Next();
                    if (o.Command == "migration-rehearsal") o.TenantSelection = tenant;
                    else o.TenantIndex = int.Parse(tenant, CultureInfo.InvariantCulture);
                    break;
                }
                case "--documents": o.Documents = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--numbering": o.NumberingMode = Next(); break;
                case "--email": o.Email = Next(); emailSpecified = true; break;
                case "--password": o.Password = Next(); break;
                case "--weights": o.Weights = Next(); break;
                case "--serials": o.Serials = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--connection": o.SourceConnectionString = ConnectionFor(Next(), environment); break;
                case "--capture-plans": o.CapturePlans = true; break;
                case "--target": o.TargetConnectionString = ConnectionFor(Next(), environment); break;
                case "--source": o.SourceConnectionString = ConnectionFor(Next(), environment); break;
            }
        }

        if (o.TenantIndex is <= 0) throw new ArgumentOutOfRangeException("--tenant", "Tenant index must be positive.");
        if (o.Documents is < 1 or > 500) throw new ArgumentOutOfRangeException("--documents", "Documents must be from 1 to 500.");
        if (o.TenantIndex is { } tenantIndex && !emailSpecified) o.Email = DevelopmentSeeder.AdministratorEmail($"bench{tenantIndex}.yojana.local");

        if (o.Command == "migration-source")
        {
            if (o.Serials is < 1 or > 5_000_000)
                throw new ArgumentOutOfRangeException("--serials", "Serial count must be from 1 to 5,000,000.");
            if (string.IsNullOrWhiteSpace(o.SourceConnectionString))
                throw new ArgumentException("migration-source needs --connection <source connection string>.");
            o.ConnectionString = o.SourceConnectionString;
            if (!outputSpecified) o.OutDirectory = "artifacts/migration-rehearsal";
            var source = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(o.SourceConnectionString);
            o.Server = source.DataSource;
            o.Database = source.InitialCatalog;
            return o;
        }
        if (o.Command == "migration-rehearsal")
        {
            if (string.IsNullOrWhiteSpace(o.TargetConnectionString) || string.IsNullOrWhiteSpace(o.SourceConnectionString))
                throw new ArgumentException("migration-rehearsal needs --target <db> and --source <db>.");
            if (string.IsNullOrWhiteSpace(o.TenantSelection))
                throw new ArgumentException("migration-rehearsal needs --tenant <new|id>.");
            if (o.TenantSelection != "new" && !Guid.TryParse(o.TenantSelection, out _))
                throw new ArgumentException("--tenant must be 'new' or a tenant GUID.");
            o.ConnectionString = o.TargetConnectionString;
            if (!outputSpecified) o.OutDirectory = "artifacts/migration-rehearsal";
            var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(o.TargetConnectionString);
            o.Server = target.DataSource;
            o.Database = target.InitialCatalog;
            return o;
        }

        var full = environment("YOJANA_BENCH_SQL");
        if (!string.IsNullOrEmpty(full))
        {
            o.ConnectionString = full;
            var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(full);
            o.Server = connection.DataSource;
            o.Database = connection.InitialCatalog;
        }
        else
        {
            o.Server = environment("YOJANA_BENCH_SQL_HOST") ?? throw new InvalidOperationException("YOJANA_BENCH_SQL_HOST (or YOJANA_BENCH_SQL) is required");
            var password = environment("YOJANA_BENCH_SQL_PASSWORD") ?? throw new InvalidOperationException("YOJANA_BENCH_SQL_PASSWORD is required");
            o.ConnectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = o.Server, InitialCatalog = o.Database, UserID = "sa", Password = password, Encrypt = true, TrustServerCertificate = true,
                ConnectTimeout = 60, MaxPoolSize = 200, ApplicationName = "yojana-bench",
            }.ConnectionString;
        }

        if (o.Command is "seed-users" or "prepare-concurrency" or "concurrency-hotpaths" && o.TenantIndex is null)
            throw new ArgumentException($"{o.Command} needs --tenant <index>");
        if (o.Command == "seed-users" && string.IsNullOrWhiteSpace(environmentPassword))
            throw new ArgumentException("seed-users needs YOJANA_BENCH_PASSWORD");
        if (o.Command is "concurrency" or "seed-users")
        {
            if (string.IsNullOrWhiteSpace(o.Password)) throw new ArgumentException($"{o.Command} needs YOJANA_BENCH_PASSWORD");
        }
        if (o.Command is "concurrency" or "prepare-concurrency")
        {
            if (o.Engineers is < 1 or > 1000) throw new ArgumentOutOfRangeException("--engineers", "Engineers must be from 1 to 1000.");
            if (o.Duration < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException("--duration", "Duration must be at least one second.");
            if (o.ContentionPercent is < 0 or > 100) throw new ArgumentOutOfRangeException("--contention", "Contention must be from 0 to 100.");
            if (o.NumberingMode is not ("sequence" or "gap_free")) throw new ArgumentException("--numbering is sequence or gap_free");
            _ = ConcurrencyWorkload.Weights(o);
        }
        if (o.Command == "concurrency" && o.Url is null)
            throw new ArgumentException("concurrency needs --url for the running Yojana web host");
        return o;
    }

    private static string ConnectionFor(string value, Func<string, string?> environment)
    {
        if (value.Contains('=', StringComparison.Ordinal)) return value;
        var basis = environment("YOJANA_TEST_SQL") ?? environment("YOJANA_BENCH_SQL")
            ?? throw new InvalidOperationException(
                "A database name needs YOJANA_TEST_SQL or YOJANA_BENCH_SQL as its server and credential template.");
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(basis) { InitialCatalog = value }.ConnectionString;
    }

    private static TimeSpan ParseDuration(string value)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        if (value.Length > 1 && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var count))
            return value[^1] switch { 's' => TimeSpan.FromSeconds(count), 'm' => TimeSpan.FromMinutes(count), 'h' => TimeSpan.FromHours(count), _ => throw new FormatException("Duration suffix is s, m or h.") };
        throw new FormatException("Duration is a TimeSpan or a value such as 60s or 10m.");
    }
}
