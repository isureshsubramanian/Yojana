using Microsoft.AspNetCore.Http.Features;
using Yojana.RenderWorker;
using Yojana.Serialisation.Contracts;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(RenderWorkerOptions.SectionName).Get<RenderWorkerOptions>()
    ?? throw new InvalidOperationException($"Configuration section {RenderWorkerOptions.SectionName} is required.");
if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var listenUri) || !listenUri.IsLoopback
    || listenUri.Scheme != Uri.UriSchemeHttp)
    throw new InvalidOperationException("RenderWorker:ListenUrl must be an absolute loopback HTTP URL.");
var installation = BrowserInstallation.Resolve(options.Browser, builder.Environment.ContentRootPath);

if (args.Contains("--install-browser", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    await BrowserInstaller.InstallAsync(options.Browser, builder.Environment.ContentRootPath, client, CancellationToken.None);
    Console.WriteLine($"Installed and verified headless shell {installation.Version} for {installation.Platform} at {installation.InstallRoot}");
    return;
}

await BrowserInstallationVerifier.EnsureValidAsync(installation, CancellationToken.None);
builder.WebHost.UseUrls(options.ListenUrl);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = 1);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(installation);
builder.Services.AddSingleton<BrowserPool>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok", browser = installation.Version }));
app.MapPost("/render", async (PdfRenderRequest request, BrowserPool pool, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Html))
        return Results.Problem(title: "render_html_required", detail: "HTML is required.", statusCode: 400);
    if (request.Html.Length > 20_000_000)
        return Results.Problem(title: "render_html_too_large", detail: "HTML exceeds the 20 MB render limit.", statusCode: 413);
    var bytes = await pool.RenderAsync(request, cancellationToken);
    return Results.File(bytes, "application/pdf");
});
app.MapGet("/notices", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Notices", "THIRD-PARTY-NOTICES.txt");
    return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8")
        : Results.Problem("notices file missing from the worker artefact", statusCode: 500);
});
await app.RunAsync();

public partial class Program;
