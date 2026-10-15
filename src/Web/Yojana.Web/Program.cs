using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Yojana.Integration;
using Yojana.Platform;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Shell;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Messaging.Smoke;
using Yojana.Platform.Seeding;
using Yojana.Platform.Security;
using Yojana.Platform.Shell;
using Yojana.Serialisation;
using Yojana.Web;

// The web client (web/, MOD-02) is built by esbuild into web/dist and copied into this host's output as
// wwwroot by Yojana.Web.csproj. The web root is therefore the copy beside the binaries in every environment
// (dotnet run and publish alike), not the project folder. Coupling declared in web/build.mjs and the csproj.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.AddYojanaPlatform();
builder.AddSerialisationModule();
builder.AddIntegrationModule();

var app = builder.Build();

// Operational commands: `--migrate` applies migrations and exits; `--seed` seeds the DEV tenant and exits.
if (args.Contains("--migrate") || args.Contains("--seed"))
{
    await PlatformSetup.MigrateAsync(app.Services, CancellationToken.None);
    if (args.Contains("--seed"))
    {
        var password = Environment.GetEnvironmentVariable("YOJANA_SEED_PASSWORD") ?? "REDACTED-dev-password";
        await DevelopmentSeeder.SeedAsync(app.Services, password, CancellationToken.None);
        app.Logger.LogInformation("Seeded tenant {Code} with one user per role (password from YOJANA_SEED_PASSWORD or the development default)", DevelopmentSeeder.DevTenantCode);
    }

    return;
}

// Hashed client assets are immutable; index.html must always be revalidated so a new build is picked up.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        headers.CacheControl = ctx.File.Name == "index.html" ? "no-cache" : "public, max-age=31536000, immutable";
    },
});
app.UseYojanaPlatform();

app.MapGet("/health", async (PlatformDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect ? Results.Ok(new { status = "ok", database = "reachable" }) : Results.Problem("database unreachable", statusCode: 503);
});

// Third-party notices ship inside the artefact and are reachable from the running application (MOD-01).
app.MapGet("/notices", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Notices", "THIRD-PARTY-NOTICES.txt");
    return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8") : Results.Problem("notices file missing from the artefact", statusCode: 500);
}).AllowAnonymous();

app.MapPost("/api/auth/sign-in", async (SignInRequest request, SignInService signIn, HttpContext http, CancellationToken ct) =>
{
    var user = await signIn.AuthenticateAsync(request.Email, request.Password, ct);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var tenants = await signIn.TenantsForAsync(user.Id, ct);
    var selected = tenants.Count == 1 ? tenants[0] : null;
    await http.SignInAsync(SignInService.PrincipalFor(user, selected));
    return Results.Ok(SessionView.From(user, selected, tenants));
}).AllowAnonymous();

app.MapPost("/api/auth/select-tenant", async (SelectTenantRequest request, SignInService signIn, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = Guid.Parse(http.User.FindFirst(YojanaClaim.UserId)!.Value);
    var tenants = await signIn.TenantsForAsync(userId, ct);
    var choice = tenants.FirstOrDefault(t => t.Code == request.TenantCode);
    if (choice is null)
    {
        return Results.Forbid();
    }

    var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
    await http.SignInAsync(SignInService.PrincipalFor(user, choice));
    return Results.Ok(SessionView.From(user, choice, tenants));
}).RequireAuthorization();

// The shell's session: who is signed in, which tenant is selected, which tenants could be.
app.MapGet("/api/auth/me", async (SignInService signIn, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = Guid.Parse(http.User.FindFirst(YojanaClaim.UserId)!.Value);
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
    if (user is null)
    {
        await http.SignOutAsync();
        return Results.Unauthorized();
    }

    var tenants = await signIn.TenantsForAsync(userId, ct);
    var code = http.User.FindFirst(YojanaClaim.TenantCode)?.Value;
    var selected = code is null ? null : tenants.FirstOrDefault(t => t.Code == code);
    return Results.Ok(SessionView.From(user, selected, tenants));
}).RequireAuthorization();

app.MapPost("/api/auth/sign-out", async (HttpContext http) => { await http.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization();

// Shell: the overview page and the header's connection health, assembled from every module's contributions.
app.MapGet("/api/shell/overview", async (ShellOverviewService shell, HttpContext http, IClock clock, CancellationToken ct) =>
{
    var context = new OverviewContext(
        Guid.Parse(http.User.FindFirst(YojanaClaim.UserId)!.Value),
        http.User.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList(),
        clock.UtcNow);
    return Results.Ok(await shell.BuildAsync(context, ct));
}).RequireAuthorization(YojanaPolicy.TenantMember);

app.MapGet("/api/shell/health", async (ShellOverviewService shell, CancellationToken ct) =>
    Results.Ok(await shell.HealthAsync(ct))).RequireAuthorization(YojanaPolicy.TenantMember);

// Smoke: publish inside the request's transaction (outbox), delivered after commit; handler and job write rows.
app.MapPost("/api/smoke/ping", async (PingRequest request, IDbContextOutbox<PlatformDbContext> outbox, ITenantContext tenant, IClock clock, CancellationToken ct) =>
{
    var db = outbox.DbContext;
    db.SmokeLogs.Add(new SmokeLog { TenantId = tenant.TenantId, Id = TimeOrderedId.New(clock.UtcNow), Source = "request", Note = request.Note, RecordedAt = clock.UtcNow });
    await outbox.PublishAsync(new SmokePing(request.Note), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
    await outbox.SaveChangesAndFlushMessagesAsync(ct);
    return Results.Accepted();
}).RequireAuthorization(YojanaPolicy.TenantMember);

app.MapGet("/api/smoke", async (PlatformDbContext db, CancellationToken ct) =>
    Results.Ok(await SmokeQueries.RecentAsync(db, 50, ct))).RequireAuthorization(YojanaPolicy.TenantMember);

// Role policy demonstration: only a serial administrator reaches this.
app.MapGet("/api/smoke/admin-only", () => Results.Ok(new { ok = true })).RequireAuthorization(YojanaPolicy.SerialAdministrator);

// Unknown API paths are 404s; every other extension-less path is the single-page client (client-side routing).
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("{*path:nonfile}", "index.html").AllowAnonymous();

await app.RunAsync();

namespace Yojana.Web
{
    public sealed record SignInRequest(string Email, string Password);

    public sealed record SelectTenantRequest(string TenantCode);

    public sealed record PingRequest(string Note);

    /// <summary>Wire shape of the signed-in session for the web shell (web/src/api/types.ts mirrors it).</summary>
    public sealed record SessionView(SessionUserView User, SessionTenantView? Tenant, IReadOnlyList<string> Roles, IReadOnlyList<TenantChoiceView> Tenants)
    {
        public static SessionView From(AppUser user, TenantChoice? selected, IReadOnlyList<TenantChoice> tenants) => new(
            new SessionUserView(user.Id, user.Email, user.DisplayName),
            selected is null ? null : new SessionTenantView(selected.Code, selected.Name),
            selected?.Roles.Where(YojanaRole.IsKnown).ToList() ?? [],
            tenants.Select(t => new TenantChoiceView(t.Code, t.Name, t.Roles)).ToList());
    }

    public sealed record SessionUserView(Guid Id, string Email, string DisplayName);

    public sealed record SessionTenantView(string Code, string Name);

    public sealed record TenantChoiceView(string Code, string Name, IReadOnlyList<string> Roles);

    /// <summary>Marker so test projects can reference the host assembly.</summary>
    public static class WebHost
    {
    }
}
