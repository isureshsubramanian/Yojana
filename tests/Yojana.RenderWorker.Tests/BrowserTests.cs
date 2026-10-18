using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yojana.RenderWorker;
using Yojana.Serialisation.Contracts;

namespace Yojana.RenderWorker.Tests;

public sealed class BrowserTests
{
    [Fact]
    public async Task Archive_hash_verifier_accepts_exact_bytes_and_rejects_a_different_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yojana-browser-hash-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "pinned-browser-archive", Encoding.UTF8);
            var hash = await BrowserHashVerifier.HashAsync(path, CancellationToken.None);

            Assert.True(await BrowserHashVerifier.MatchesAsync(path, hash, CancellationToken.None));
            Assert.False(await BrowserHashVerifier.MatchesAsync(path, new string('0', 64), CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [BrowserInstalledFact]
    public async Task Installed_browser_renders_searchable_two_page_pdf()
    {
        var (options, installation) = TestBrowser.Load();
        await BrowserInstallationVerifier.EnsureValidAsync(installation, CancellationToken.None);
        await using var pool = new BrowserPool(options, installation, NullLoggerFactory.Instance);
        const string tracking = "TRACK-2026-001";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + tracking +
            "</title><style>section{font:16px monospace}section:first-child{break-after:page}</style></head>" +
            "<body><section>COPY Certificate " + tracking + "</section><section>Signature block Alex Reviewer " + tracking + "</section></body></html>";

        var pdf = await pool.RenderAsync(new PdfRenderRequest(html,
            new PdfPageSpec("A4", "portrait", 12, 12, 12, 12, null,
                "<div>Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>")),
            CancellationToken.None);

        Assert.True(pdf.AsSpan().StartsWith("%PDF-"u8));
        Assert.Equal(2, Regex.Count(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b"));
        var text = await PdfTextAsync(pdf);
        Assert.Contains(tracking, text, StringComparison.Ordinal);
        Assert.Contains("COPY", text, StringComparison.Ordinal);
        Assert.Contains("Alex Reviewer", text, StringComparison.Ordinal);
    }

    private static async Task<string> PdfTextAsync(byte[] pdf)
    {
        var start = new ProcessStartInfo("pdftotext")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-");
        start.ArgumentList.Add("-");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pdftotext.");
        await process.StandardInput.BaseStream.WriteAsync(pdf);
        process.StandardInput.Close();
        var text = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return text;
    }
}

public sealed class BrowserInstalledFactAttribute : FactAttribute
{
    public BrowserInstalledFactAttribute()
    {
        try
        {
            var (_, installation) = TestBrowser.Load();
            if (!File.Exists(installation.ExecutablePath) || !File.Exists(installation.ReceiptPath))
                Skip = "Pinned render-worker browser is not installed; run `make browser`.";
            else if (!ExecutableOnPath("pdftotext"))
                Skip = "pdftotext is required for the package-free searchable-text assertion.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
        {
            Skip = $"Render-worker integration prerequisites are unavailable: {exception.Message}";
        }
    }

    private static bool ExecutableOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, name)));
}

internal static class TestBrowser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static (RenderWorkerOptions Options, BrowserInstallation Installation) Load()
    {
        var root = FindRoot(AppContext.BaseDirectory);
        var worker = Path.Combine(root, "src", "Workers", "Yojana.RenderWorker");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(worker, "appsettings.json")));
        var options = json.RootElement.GetProperty(RenderWorkerOptions.SectionName).Deserialize<RenderWorkerOptions>(JsonOptions)
            ?? throw new InvalidOperationException("RenderWorker configuration is invalid.");
        return (options, BrowserInstallation.Resolve(options.Browser, worker));
    }

    private static string FindRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
