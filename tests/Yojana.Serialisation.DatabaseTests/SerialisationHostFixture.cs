using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yojana.Platform;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Jurisdictions;
using Wolverine;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// Platform plus Serialisation module against the database named by YOJANA_TEST_SQL (migrated on start).
/// Tests are skipped, not failed, when the variable is absent so `make test` stays database-free.
/// </summary>
public sealed class SerialisationHostFixture : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL");

    public IHost Host { get; private set; } = null!;

    public IServiceProvider Services => Host.Services;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Platform:ConnectionString"] = ConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:ServiceName"] = "yojana-serialisation-tests";
        builder.Configuration[$"{CertificateRunOptions.SectionName}:MaximumSynchronousUnits"] = "120";
        builder.Configuration[$"{Yojana.Serialisation.Ledger.LedgerBaseOrderBackfillOptions.SectionName}:BatchSize"] = "1";
        builder.Configuration[$"{Yojana.Serialisation.Ledger.LedgerBaseOrderBackfillOptions.SectionName}:PauseMilliseconds"] = "0";
        PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(SerialisationHostFixture).Assembly); // the published-event sink
        builder.AddYojanaPlatform();
        builder.Services.AddSingleton<TestCertificateRunChunkObserver>();
        builder.Services.AddSingleton<ICertificateRunChunkObserver>(sp => sp.GetRequiredService<TestCertificateRunChunkObserver>());
        builder.AddSerialisationModule();
        builder.Services.ConfigureWolverine(options =>
        {
            // TIGHT COUPLING (test sink endpoint configuration)
            // WHAT: this names Wolverine's convention-derived endpoints for PublishedEventSinkHandler.Handle methods.
            // WHY: the sink only appends to ConcurrentQueues and its tests filter by ids; it is not recoverable work,
            // so persisting an inbound copy adds envelope contention without testing another contract. WHAT BREAKS IF
            // SEPARATED: the probe queues behind certificate load and times out despite committed events. Keep the
            // matching sink convention/comment in step with this.
            foreach (var queueName in PublishedEventSink.ObservationQueueNames)
            {
                options.LocalQueue(queueName).BufferedInMemory()
                    .MaximumParallelMessages(PublishedEventSink.MaximumParallelMessages);
            }
        });
        var app = builder.Build();
        app.UseApiProblemDetails();
        app.UseYojanaPlatform();
        app.UseSerialisationInteractiveReads();
        app.MapSerialisationEndpoints();
        app.MapGet("/test/sign-in/{tenantId:guid}", async (Guid tenantId, HttpContext http) =>
        {
            var userId = Guid.NewGuid();
            var claims = new List<Claim>
            {
                new(YojanaClaim.UserId, userId.ToString("D")),
                new(ClaimTypes.NameIdentifier, userId.ToString("D")),
                new(ClaimTypes.Name, "Fictional Quality User"),
                new(ClaimTypes.Email, "quality.user@example.invalid"),
                new(YojanaClaim.TenantId, tenantId.ToString("D")),
                new(YojanaClaim.TenantCode, "http-test"),
            };
            claims.AddRange(YojanaRole.All.Select(role => new Claim(YojanaClaim.Role, role)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims,
                CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, YojanaClaim.Role));
            await http.SignInAsync(principal);
            return Results.NoContent();
        }).AllowAnonymous();
        app.MapGet("/api/test/unhandled", (Func<IResult>)(() =>
                throw new InvalidOperationException("secret stack and cookie must not escape")))
            .AllowAnonymous();
        Host = app;
        await app.StartAsync();
    }

    public async Task<HttpClient> AuthenticatedClientAsync(TenantId tenant)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = Address() };
        using var response = await client.GetAsync($"/test/sign-in/{tenant.Value:D}");
        response.EnsureSuccessStatusCode();
        return client;
    }

    public HttpClient Client() => new() { BaseAddress = Address() };

    private Uri Address()
    {
        var server = Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new Uri(address);
    }

    public async Task DisposeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await Host.StopAsync();
        Host.Dispose();
    }

    /// <summary>A scope serving one tenant (or none).</summary>
    public IServiceScope ScopeFor(TenantId? tenant)
    {
        var scope = Services.CreateScope();
        if (tenant is { } id)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(id);
        }

        return scope;
    }

    public async Task<TenantId> NewTenantAsync(string prefix, bool importJurisdictionStarter = true)
    {
        var id = new TenantId(Guid.NewGuid());
        using (var scope = ScopeFor(null))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Keep at least eight random characters: a prefix that filled the 20-character code alone repeated across runs.
            var stem = prefix.Length > 12 ? prefix[..12] : prefix;
            db.Tenants.Add(new Tenant { Id = id, Code = stem + Guid.NewGuid().ToString("N")[..(20 - stem.Length)], Name = "test " + prefix, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // Existing scenarios use EU-scoped policies and therefore opt into the starter through their common tenant
        // arrangement. Jurisdiction catalogue tests pass false when they need to prove a genuinely neutral tenant.
        if (importJurisdictionStarter) await ImportEuStarterAsync(id);
        return id;
    }

    /// <summary>A tenant set up with its working currency and the other currencies it accepts on documents.</summary>
    public async Task<TenantId> NewTenantAsync(string prefix, string currency, params string[] accepted)
    {
        var id = await NewTenantAsync(prefix);
        using var scope = ScopeFor(id);
        var currencies = scope.ServiceProvider.GetRequiredService<ITenantCurrency>();
        await currencies.SetAsync(currency, CancellationToken.None);
        await currencies.SetAcceptedAsync(accepted, CancellationToken.None);
        return id;
    }

    /// <summary>Opt-in to the P6 baseline pack for arrangements that intentionally exercise the former EU behaviour.</summary>
    public async Task ImportEuStarterAsync(TenantId tenant)
    {
        using var scope = ScopeFor(tenant);
        var catalogue = scope.ServiceProvider.GetRequiredService<JurisdictionCatalogueService>();
        var pack = (await catalogue.CatalogueAsync(null, null, CancellationToken.None)).RulePacks
            .Single(row => row.Code == "eu-starter");
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<RulePackImported>(
            new ImportRulePack(Guid.NewGuid(), pack.RulePackId, "test:legal-admin"),
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }
}

[CollectionDefinition("serialisation host")]
public sealed class SerialisationHostCollection : ICollectionFixture<SerialisationHostFixture>;

/// <summary>Marks a fact that needs the database; skipped when YOJANA_TEST_SQL is not set.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (SerialisationHostFixture.ConnectionString is null)
        {
            Skip = "YOJANA_TEST_SQL is not set";
        }
    }
}

/// <summary>Marks a theory that needs the database; skipped when YOJANA_TEST_SQL is not set.</summary>
public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute()
    {
        if (SerialisationHostFixture.ConnectionString is null)
        {
            Skip = "YOJANA_TEST_SQL is not set";
        }
    }
}
