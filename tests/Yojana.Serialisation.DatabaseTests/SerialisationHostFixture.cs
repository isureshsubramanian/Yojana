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
using Yojana.Integration.Contracts;
using Yojana.Serialisation;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Claims.Risk;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Jurisdictions;
using Yojana.Serialisation.Settings;
using Wolverine;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// Platform plus Serialisation module against the database named by YOJANA_TEST_SQL (migrated on start).
/// Tests are skipped, not failed, when the variable is absent so `make test` stays database-free.
/// </summary>
public sealed class SerialisationHostFixture : IAsyncLifetime
{
    public static readonly string ImportFileRoot = Path.Combine(Path.GetTempPath(), "yojana-import-files-tests");
    public static readonly string ImportFileArchiveRoot = Path.Combine(Path.GetTempPath(), "yojana-import-files-tests-archive");
    public static readonly string MailPickupDirectory = Path.Combine(Path.GetTempPath(), $"yojana-serialisation-mail-{Guid.NewGuid():N}");
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL");
    public static string? ImportFilesConnectionString => ConnectionString is null ? null : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString)
    {
        InitialCatalog = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString).InitialCatalog + "_files"
    }.ConnectionString;
    public static string? ImportFilesArchiveConnectionString => ConnectionString is null ? null : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString)
    {
        InitialCatalog = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString).InitialCatalog + "_files_archive"
    }.ConnectionString;

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
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:DatabaseConnectionString"] = ImportFilesConnectionString;
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:FileRoot"] =
            ImportFileRoot;
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:0:Name"] = "folder-archive";
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:0:Kind"] = "folder";
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:0:Path"] =
            ImportFileArchiveRoot;
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:1:Name"] = "database-archive";
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:1:Kind"] = "database";
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:1:ConnectionString"] = ImportFilesArchiveConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "yojana-serialisation-tests";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "serialisation-database-tests-only-hmac-key";
        // Customer e-mail (P9-T20) is delivered as .eml files, so tests can read what a recipient would receive.
        builder.Configuration["Platform:Mail:Transport"] = "pickup";
        builder.Configuration["Platform:Mail:PickupDirectory"] = MailPickupDirectory;
        builder.Configuration["Platform:Mail:FromAddress"] = "no-reply@example.invalid";
        builder.Configuration["Platform:Mail:LinkBaseUrl"] = "http://127.0.0.1:5080";
        builder.Configuration[$"{CertificateRunOptions.SectionName}:MaximumSynchronousUnits"] = "120";
        builder.Configuration[$"{Yojana.Serialisation.Ledger.LedgerBaseOrderBackfillOptions.SectionName}:BatchSize"] = "1";
        builder.Configuration[$"{Yojana.Serialisation.Ledger.LedgerBaseOrderBackfillOptions.SectionName}:PauseMilliseconds"] = "0";
        PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(SerialisationHostFixture).Assembly); // the published-event sink
        builder.AddYojanaPlatform();
        builder.Services.AddSingleton<TestImportFileArchiveObserver>();
        builder.Services.AddSingleton<Yojana.Serialisation.Imports.IImportFileArchiveObserver>(sp =>
            sp.GetRequiredService<TestImportFileArchiveObserver>());
        builder.Services.AddSingleton<TestCertificateRunChunkObserver>();
        builder.Services.AddSingleton<ICertificateRunChunkObserver>(sp => sp.GetRequiredService<TestCertificateRunChunkObserver>());
        builder.AddSerialisationModule();
        builder.Services.AddSingleton<IErpBomCapabilityPort, TestErpBomCapabilityPort>();
        builder.Services.AddDbContext<Yojana.Serialisation.Data.SerialisationDbContext>(options => options.AddInterceptors(PolicyCommandCounter.Instance), optionsLifetime: ServiceLifetime.Singleton);
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
        app.MapGet("/test/sign-in/{tenantId:guid}", async (Guid tenantId, string? role, Guid? asUser,
            PlatformDbContext platform, HttpContext http) =>
        {
            var userId = asUser ?? Guid.NewGuid();
            var selectedTenant = new TenantId(tenantId);
            var grantedRoles = (role is null ? YojanaRole.All : [role]).ToList();
            var user = await platform.Users.Include(row => row.Memberships)
                .FirstOrDefaultAsync(row => row.Id == userId);
            if (user is null)
            {
                user = new AppUser
                {
                    Id = userId,
                    Email = $"test-{userId:N}@example.invalid",
                    DisplayName = "Fictional Quality User",
                    CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                };
                platform.Users.Add(user);
            }
            var membership = user.Memberships.FirstOrDefault(row => row.TenantId == selectedTenant);
            if (membership is null)
            {
                user.Memberships.Add(new UserTenantMembership
                {
                    UserId = userId, TenantId = selectedTenant, Roles = grantedRoles,
                });
            }
            else
            {
                membership.Roles = grantedRoles;
            }
            await platform.SaveChangesAsync();
            var claims = new List<Claim>
            {
                new(YojanaClaim.UserId, userId.ToString("D")),
                new(YojanaClaim.SecurityStamp, user.SecurityStamp.ToString("D")),
                new(YojanaClaim.SessionId, Guid.NewGuid().ToString("D")),
                new(ClaimTypes.NameIdentifier, userId.ToString("D")),
                new(ClaimTypes.Name, "Fictional Quality User"),
                new(ClaimTypes.Email, "quality.user@example.invalid"),
                new(YojanaClaim.TenantId, tenantId.ToString("D")),
                new(YojanaClaim.TenantCode, "http-test"),
            };
            claims.AddRange(grantedRoles.Select(grantedRole => new Claim(YojanaClaim.Role, grantedRole)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims,
                CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, YojanaClaim.Role));
            await http.SignInAsync(principal);
            return Results.NoContent();
        }).AllowAnonymous();
        app.MapGet("/api/test/unhandled", (Func<IResult>)(() =>
                throw new InvalidOperationException("secret stack and cookie must not escape")))
            .AllowAnonymous();
        app.MapPost("/api/test/body", (ProblemTestBody body) => Results.NoContent()).AllowAnonymous();
        Host = app;
        await app.StartAsync();
    }

    public async Task<HttpClient> AuthenticatedClientAsync(TenantId tenant, string? role = null, Guid? userId = null)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = Address() };
        var query = new List<string>();
        if (role is not null) query.Add($"role={Uri.EscapeDataString(role)}");
        if (userId is not null) query.Add($"asUser={userId.Value:D}");
        var queryText = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
        using var response = await client.GetAsync($"/test/sign-in/{tenant.Value:D}{queryText}");
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

    public async Task EnableErpBomCompletenessAsync(TenantId tenant)
    {
        using var scope = ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .SetAsync(ErpBomCompletenessSettings.Key, bool.TrueString, CancellationToken.None);
    }

    /// <summary>A request-equivalent scope whose SQL connections carry the customer party RLS key.</summary>
    public IServiceScope ScopeForPortal(TenantId tenant, Guid partyRef)
    {
        var scope = ScopeFor(tenant);
        scope.ServiceProvider.GetRequiredService<IPortalPartyContextSetter>().SetPortalParty(partyRef);
        return scope;
    }

    public Task<TenantId> NewTenantAsync(string prefix, bool importJurisdictionStarter = true) =>
        NewTenantCoreAsync(prefix, "EUR", importJurisdictionStarter);

    public Task<TenantId> NewTenantWithoutCurrencyAsync(string prefix, bool importJurisdictionStarter = true) =>
        NewTenantCoreAsync(prefix, null, importJurisdictionStarter);

    private async Task<TenantId> NewTenantCoreAsync(string prefix, string? currency, bool importJurisdictionStarter)
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
        // Existing scenarios record EUR amounts. Configure their home currency before importing priced jurisdiction
        // rules; tests that exercise pre-configuration explicitly use NewTenantWithoutCurrencyAsync.
        if (currency is not null)
        {
            using var scope = ScopeFor(id);
            await scope.ServiceProvider.GetRequiredService<ITenantCurrency>().SetAsync(currency, CancellationToken.None);
        }
        // Existing scenarios use EU-scoped policies and therefore opt into the starter through their common tenant
        // arrangement. Jurisdiction catalogue tests pass false when they need to prove a genuinely neutral tenant.
        if (importJurisdictionStarter) await ImportEuStarterAsync(id);
        // Existing scenario tests pre-date P9-T18 and assert claim adjudication in isolation. Risk-specific tests opt
        // individual rules back in; keeping the common fixture neutral avoids turning every older claim into review data.
        using (var scope = ScopeFor(id))
        {
            var defaults = ClaimRiskRulesSettings.DefaultPolicy;
            await scope.ServiceProvider.GetRequiredService<ClaimRiskRulesSettings>().SetAsync(defaults with
            {
                RecentCoverPurchase = defaults.RecentCoverPurchase with { Enabled = false },
                RepeatUnitClaims = defaults.RepeatUnitClaims with { Enabled = false },
                RepeatFault = defaults.RepeatFault with { Enabled = false },
                ClaimantRate = defaults.ClaimantRate with { Enabled = false },
                ChannelMismatch = defaults.ChannelMismatch with { Enabled = false },
                RecentOwnershipChange = defaults.RecentOwnershipChange with { Enabled = false },
                PartNotReturned = defaults.PartNotReturned with { Enabled = false },
                DuplicateOpenClaim = defaults.DuplicateOpenClaim with { Enabled = false },
            }, CancellationToken.None);
        }
        return id;
    }

    /// <summary>A tenant set up with its home currency. The trailing values are tolerated for old fixture callers.</summary>
    public async Task<TenantId> NewTenantAsync(string prefix, string currency, params string[] _)
    {
        return await NewTenantCoreAsync(prefix, currency, true);
    }

    public Task<TenantId> NewTenantAsync(string prefix, string currency, bool importJurisdictionStarter) =>
        NewTenantCoreAsync(prefix, currency, importJurisdictionStarter);

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

internal sealed class TestErpBomCapabilityPort : IErpBomCapabilityPort
{
    public Task<bool> HasActiveBomSourceAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

[CollectionDefinition("serialisation host", DisableParallelization = true)]
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

public sealed record ProblemTestBody(string Name, int Count);
