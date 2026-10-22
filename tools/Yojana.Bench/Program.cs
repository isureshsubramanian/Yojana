// P1-T21 storage benchmark (ADR-0001 AI 1, 53). Loads the reference layout at scale on a named SQL Server through the
// real schema (migrations), with row-level security on, then measures the SLO table through the module's own
// services and through server-side timing loops. Usage:
//   dotnet run --project tools/Yojana.Bench -- prepare|load|measure|backfill|entitlements|warranty|all [--units N] [--events N] [--tenants N]
//   dotnet run --project tools/Yojana.Bench -- ledger-base-order-backfill|read-shapes
//   dotnet run --project tools/Yojana.Bench -- seed-users|prepare-concurrency --tenant N [--documents N]
//   dotnet run --project tools/Yojana.Bench -- concurrency [--tenant N] --url URL [--engineers N] [--duration 10m] [--numbering sequence|gap_free]
// Connection: YOJANA_BENCH_SQL_HOST and YOJANA_BENCH_SQL_PASSWORD (sa), or YOJANA_BENCH_SQL as a full connection string.
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yojana.Bench;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Seeding;
using Yojana.Platform.Tenancy;
using Yojana.Platform.Time;
using Yojana.Serialisation;

var options = BenchOptions.Parse(args);
if (options.Command == "migration-source")
{
    var generated = await new MigrationSourceGenerator(options.SourceConnectionString!, options.Serials, options.Seed)
        .GenerateAsync(CancellationToken.None);
    Console.WriteLine($"migration source · serials {generated.RequestedSerials:N0} · rows {generated.RowsWritten:N0} · hash {generated.ContentHash}");
    foreach (var row in generated.RowsByRole.OrderBy(row => row.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {row.Key}: {row.Value:N0}");
    return;
}
if (options.Command == "migration-rehearsal")
{
    try
    {
        var result = await new MigrationRehearsalRunner(options).RunAsync(CancellationToken.None);
        Console.WriteLine($"migration rehearsal complete · tenant {result.TenantId:D} · source {result.SourceSerials:N0} = loaded {result.Loaded:N0} + refused {result.Refused:N0} + excluded {result.Excluded:N0}");
        Console.WriteLine($"reports: {result.MarkdownReport} · {result.JsonReport}");
    }
    catch (MigrationRehearsalException exception)
    {
        Console.Error.WriteLine($"migration rehearsal failed: {exception.Message}");
        Console.Error.WriteLine($"failure reports: {exception.MarkdownReport} · {exception.JsonReport}");
        Environment.ExitCode = 1;
    }
    return;
}
if (options.Command == "concurrency")
{
    await new ConcurrencyRunner(options).RunAsync(CancellationToken.None);
    return;
}
if (options.Command == "concurrency-hotpaths")
{
    await new ConcurrencyHotPathVerifier(options).RunAsync(CancellationToken.None);
    return;
}
if (options.Command == "ping")
{
    // Connectivity probe without the host: which encryption settings reach the server from this client.
    foreach (var encrypt in new[] { "Mandatory", "Optional", "Strict" })
    {
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.ConnectionString) { ConnectTimeout = 15 };
        csb.Encrypt = encrypt switch { "Optional" => Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, "Strict" => Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Strict, _ => Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory };
        csb.InitialCatalog = "master";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var c = new Microsoft.Data.SqlClient.SqlConnection(csb.ConnectionString);
            await c.OpenAsync();
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100))", c);
            Console.WriteLine($"encrypt={encrypt}: ok in {sw.ElapsedMilliseconds} ms → {await cmd.ExecuteScalarAsync()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"encrypt={encrypt}: FAILED after {sw.ElapsedMilliseconds} ms → {ex.Message.Split('\n')[0]}");
        }
    }

    return;
}
Console.WriteLine($"yojana bench · {options.Command} · {options.Server} / {options.Database} · units {options.Units:N0} · events {options.Events:N0} · tenants {options.Tenants}");

if (options.Command == "seed-users")
{
    var seedBuilder = Host.CreateApplicationBuilder();
    seedBuilder.Logging.ClearProviders();
    seedBuilder.Services.AddScoped<TenantContext>();
    seedBuilder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
    seedBuilder.Services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
    seedBuilder.Services.AddSingleton<IClock, SystemClock>();
    seedBuilder.Services.AddDbContext<PlatformDbContext>(db => db.UseSqlServer(options.ConnectionString,
        sql => sql.MigrationsHistoryTable("__migrations", PlatformDbContext.SchemaName)));
    using var seedHost = seedBuilder.Build();
    var index = options.TenantIndex!.Value;
    await DevelopmentSeeder.SeedTenantUsersAsync(seedHost.Services,
        new TenantUserSeed(new TenantId(options.ConcurrencyTenantId), $"BENCH{index}", $"Benchmark tenant {index}",
            $"bench{index}.yojana.local", $"Benchmark tenant {index} administrator"),
        options.Password, CancellationToken.None);
    Console.WriteLine($"seeded platform tenant BENCH{index} and {Yojana.Platform.Contracts.Security.YojanaRole.All.Count + 1} role accounts");
    return;
}

var builder = Host.CreateApplicationBuilder();
builder.Configuration["Platform:ConnectionString"] = options.ConnectionString;
builder.Configuration["Platform:MigrateOnStartup"] = "true";
builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
builder.Configuration["Platform:ServiceName"] = "yojana-bench";
// Import files live in their own database (OD-33); the bench never stores files, but the module requires the connection.
builder.Configuration["Serialisation:ImportFiles:DatabaseConnectionString"] =
    new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.ConnectionString) { InitialCatalog = options.Database + "_files" }.ConnectionString;
builder.Configuration["Serialisation:ImportFiles:FileRoot"] = Path.Combine(Path.GetTempPath(), "yojana-import-files-bench");
builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Name"] = "long-term";
builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Kind"] = "database";
builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:ConnectionString"] =
    new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.ConnectionString) { InitialCatalog = options.Database + "_files_archive" }.ConnectionString;
builder.Configuration["Serialisation:PartitionLifecycle:MonthsAhead"] = "12";
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Yojana", LogLevel.Information);
PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
builder.AddYojanaPlatform();
builder.AddSerialisationModule();
using var host = builder.Build();
await host.StartAsync();

if (options.Command == "prepare-concurrency")
{
    try { await new ConcurrencyPreparation(host.Services, options).RunAsync(CancellationToken.None); }
    finally { await host.StopAsync(); }
    return;
}

var report = new BenchReport(options);
try
{
    if (options.Command == "heal")
    {
        // The reconciliation SLO on the full tenant: set-based compare of every row plus the folded sample.
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Yojana.Platform.Tenancy.ITenantContextSetter>().Set(new Yojana.Platform.Contracts.Tenancy.TenantId(BenchOptions.TenantId(1)));
        scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Data.SerialisationDbContext>().Database.SetCommandTimeout(7200);
        var healer = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Projections.UnitCurrentHealer>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await healer.RunAsync(false, 5000, CancellationToken.None);
        sw.Stop();
        report.Measure("Nightly stock-vs-serial reconciliation (set-based compare of every row, folded sample of 5,000)", "≤ 10 min for 1 M units", $"{r.UnitsChecked:N0} units compared, {r.Stale} stale, {r.UnitsFolded:N0} folded in {BenchReport.Ms(sw.Elapsed.TotalMilliseconds)}", sw.Elapsed.TotalMinutes <= 10 ? "PASS" : "FAIL", $"{r.Divergent.Count} divergent (synthetic events carry no state deltas, so every folded unit reports no_creating_event: an artefact of the generator, not of the ledger)");
    }

    if (options.Command is "prepare" or "all")
    {
        await new Preparer(host.Services, options, report).RunAsync();
    }

    if (options.Command is "load" or "all")
    {
        await new Loader(options, report).RunAsync();
    }

    if (options.Command is "backfill" or "all")
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Yojana.Platform.Tenancy.ITenantContextSetter>().Set(new Yojana.Platform.Contracts.Tenancy.TenantId(BenchOptions.TenantId(1)));
        var result = await scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Search.SearchBackfill>().BackfillUnitsAsync(CancellationToken.None);
        report.Measure("Set-based search backfill", "informative", $"{result.Units:N0} units in {result.Elapsed.TotalSeconds:N1} s ({result.Units / Math.Max(0.001, result.Elapsed.TotalSeconds):N0} units/s)", "INFO", $"{result.Batches:N0} batches");
    }

    if (options.Command is "entitlements" or "all")
    {
        await new EntitlementLoader(options, report).RunAsync();
    }

    if (options.Command is "measure" or "all")
    {
        await new Measurer(host.Services, options, report).RunAsync();
    }

    if (options.Command == "lifecycle")
    {
        var service = host.Services.GetRequiredService<Yojana.Serialisation.Ledger.PartitionLifecycleService>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await service.RunAsync(DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);
        sw.Stop();
        report.Measure("Partition lifecycle run (split ahead, switch cold partitions, merge up to four boundaries)", "informative", BenchReport.Ms(sw.Elapsed.TotalMilliseconds), "INFO", $"{r.BoundariesAdded.Count} boundaries added, {r.PartitionsArchived.Count} partitions archived, {r.BoundariesMerged.Count} merged ({string.Join(", ", r.BoundariesMerged.Select(b => b.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)))})");
    }

    if (options.Command == "writes")
    {
        await new Measurer(host.Services, options, report).WritesOnlyAsync();
    }

    if (options.Command == "read-shapes")
    {
        await new ReadShapeMeasurer(options, host.Services).RunAsync(CancellationToken.None);
    }

    if (options.Command == "ledger-base-order-backfill")
    {
        await new ReadShapeMeasurer(options, host.Services).BackfillAsync(CancellationToken.None);
    }

    if (options.Command == "seek")
    {
        await new Measurer(host.Services, options, report).SeeksOnlyAsync();
    }

    if (options.Command == "warranty")
    {
        await new Measurer(host.Services, options, report).WarrantyOnlyAsync();
    }

    await report.WriteAsync();
}
finally
{
    await host.StopAsync();
}
