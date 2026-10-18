using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Xunit;
using Xunit.Abstractions;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Scheduling;
using Yojana.Web;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>The executable host must build every endpoint RequestDelegate, not merely compile its mapping source.</summary>
[Collection("serialisation host")]
public sealed class WebHostStartupTests(SerialisationHostFixture fixture, ITestOutputHelper testOutput)
{
    [DatabaseFact]
    public async Task Development_host_builds_the_real_endpoint_graph_and_serves_health()
    {
        var tenants = await SeedActiveTenantsAsync(1000);
        var port = FreePort();
        var output = new List<string>();
        var start = new ProcessStartInfo("dotnet", $"\"{typeof(WebHost).Assembly.Location}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Platform__ConnectionString"] = SerialisationHostFixture.ConnectionString;
        start.Environment["Platform__MigrateOnStartup"] = "false";
        start.Environment["Platform__AutoBuildMessageStorage"] = "false";
        start.Environment["Platform__ServiceName"] = "yojana-serialisation-tests";

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) => { if (args.Data is { } line) lock (output) output.Add(line); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) lock (output) output.Add(line); };
        try
        {
            var started = Stopwatch.StartNew();
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        testOutput.WriteLine("/health became ready with 1,000 active tenants in {0:F3} seconds.", started.Elapsed.TotalSeconds);
                        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"Health became ready after {started.Elapsed}.");
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Kestrel is still binding or the endpoint graph is still being built.
                }
                catch (TaskCanceledException)
                {
                    // The short probe timeout keeps this startup gate fast.
                }
                await Task.Delay(100);
            }

            string details;
            lock (output) details = string.Join(Environment.NewLine, output.TakeLast(80));
            Assert.Fail($"The real web host did not reach /health within 20 seconds. Exit code: {(process.HasExited ? process.ExitCode : null)}{Environment.NewLine}{details}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await RemoveTenantsAndJobsAsync(tenants);
        }
    }

    private async Task<List<TenantId>> SeedActiveTenantsAsync(int count)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var rows = Enumerable.Range(1, count).Select(index => new Tenant
        {
            Id = new TenantId(Guid.NewGuid()),
            Code = $"startup-{index:D4}-{suffix}",
            Name = $"Aster startup tenant {index}",
            CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        }).ToList();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(row => row.Id).ToList();
    }

    private async Task RemoveTenantsAndJobsAsync(IReadOnlyCollection<TenantId> tenants)
    {
        var definitions = fixture.Services.GetServices<RecurringTenantJob>().ToList();
        var keys = tenants.SelectMany(tenant => definitions.Select(job => TenantJob.KeyFor(job.JobName, tenant))).ToList();
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.DeleteJobs(keys, CancellationToken.None);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Tenants.Where(row => tenants.Contains(row.Id)).ExecuteDeleteAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
