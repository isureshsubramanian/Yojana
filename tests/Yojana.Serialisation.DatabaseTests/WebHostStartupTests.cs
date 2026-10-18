using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;
using Yojana.Web;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>The executable host must build every endpoint RequestDelegate, not merely compile its mapping source.</summary>
[Collection("serialisation host")]
public sealed class WebHostStartupTests
{
    [DatabaseFact]
    public async Task Development_host_builds_the_real_endpoint_graph_and_serves_health()
    {
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
        start.Environment["Platform__ServiceName"] = "yojana-web-startup-test";

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) => { if (args.Data is { } line) lock (output) output.Add(line); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) lock (output) output.Add(line); };
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                    if (response.StatusCode == HttpStatusCode.OK) return;
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
            Assert.Fail($"The real web host did not reach /health. Exit code: {(process.HasExited ? process.ExitCode : null)}{Environment.NewLine}{details}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
