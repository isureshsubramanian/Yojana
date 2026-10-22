using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.SqlClient;
using Yojana.Integration;
using Yojana.Knowledge;
using Yojana.Knowledge.Questions;
using Yojana.Platform;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation;
using Yojana.Serialisation.Imports;

namespace Yojana.Knowledge.DatabaseTests;

public sealed class KnowledgeHostFixture : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL");
    public MutableKnowledgeClock Clock { get; } = new(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
    public WebApplication Host { get; private set; } = null!;
    public IServiceProvider Services => Host.Services;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Platform:ConnectionString"] = ConnectionString;
        builder.Configuration[$"{ImportFileStorageOptions.SectionName}:DatabaseConnectionString"] = WithDatabase("_files");
        builder.Configuration[$"{ImportFileStorageOptions.SectionName}:FileRoot"] = Path.Combine(Path.GetTempPath(), "yojana-knowledge-imports");
        builder.Configuration[$"{ImportFileStorageOptions.SectionName}:ArchiveStores:0:Name"] = "folder-archive";
        builder.Configuration[$"{ImportFileStorageOptions.SectionName}:ArchiveStores:0:Kind"] = "folder";
        builder.Configuration[$"{ImportFileStorageOptions.SectionName}:ArchiveStores:0:Path"] = Path.Combine(Path.GetTempPath(), "yojana-knowledge-imports-archive");
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "yojana-knowledge-tests";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "knowledge-database-tests-only-hmac-key";
        ConfigureLocalKey(builder.Configuration);
        PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(IntegrationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(KnowledgeModule).Assembly);
        builder.AddYojanaPlatform();
        builder.Services.AddSingleton<IClock>(Clock);
        builder.AddSerialisationModule();
        builder.AddIntegrationModule();
        builder.AddKnowledgeModule();
        if (!await FullTextInstalledAsync())
            builder.Services.Replace(ServiceDescriptor.Scoped<IQuestionTextSearch, TestOnlyLikeQuestionSearch>());
        var app = builder.Build();
        app.UseApiProblemDetails();
        app.UseYojanaPlatform();
        app.MapKnowledgeEndpoints();
        app.MapGet("/test/sign-in/{tenantId:guid}", async (Guid tenantId, Guid userId, bool? portal,
            PlatformDbContext platform, HttpContext http) =>
        {
            var tenant = new TenantId(tenantId);
            var user = await platform.Users.Include(x => x.Memberships).SingleAsync(x => x.Id == userId);
            var membership = user.Memberships.Single(x => x.TenantId == tenant);
            var claims = new List<Claim>
            {
                new(YojanaClaim.UserId, userId.ToString("D")),
                new(YojanaClaim.SecurityStamp, user.SecurityStamp.ToString("D")),
                new(YojanaClaim.SessionId, Guid.NewGuid().ToString("D")),
                new(ClaimTypes.NameIdentifier, userId.ToString("D")),
                new(YojanaClaim.TenantId, tenantId.ToString("D")),
                new(YojanaClaim.TenantCode, "knowledge-test"),
            };
            claims.AddRange(membership.Roles.Select(role => new Claim(YojanaClaim.Role, role)));
            if (portal == true) claims.Add(new Claim(YojanaClaim.CustomerPartyRef, Guid.NewGuid().ToString("D")));
            await http.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims,
                CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, YojanaClaim.Role)));
            return Results.NoContent();
        }).AllowAnonymous();
        Host = app;
        await app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (ConnectionString is null) return;
        await Host.StopAsync();
        await Host.DisposeAsync();
    }

    public IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    public async Task<(TenantId Tenant, Guid Administrator, Guid Staff, Guid Other)> NewTenantAsync(string prefix)
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var administrator = Guid.CreateVersion7();
        var staff = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant { Id = tenant, Code = prefix + Guid.NewGuid().ToString("N")[..8], Name = "Fictional " + prefix, CreatedAt = Clock.UtcNow });
        AddUser(db, tenant, administrator, "Fictional Administrator", [YojanaRole.TenantAdministrator]);
        AddUser(db, tenant, staff, "Fictional Service Lead", [YojanaRole.ServiceSupervisor]);
        AddUser(db, tenant, other, "Fictional Technician", [YojanaRole.Technician]);
        await db.SaveChangesAsync();
        return (tenant, administrator, staff, other);
    }

    public async Task<HttpClient> ClientAsync(TenantId tenant, Guid userId, bool portal = false)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = new Uri(Host.Urls.Single()) };
        var response = await client.GetAsync($"/test/sign-in/{tenant.Value:D}?userId={userId:D}&portal={portal.ToString().ToLowerInvariant()}");
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static void AddUser(PlatformDbContext db, TenantId tenant, Guid id, string name, List<string> roles)
    {
        db.Users.Add(new AppUser
        {
            Id = id, Email = $"{id:N}@example.invalid", DisplayName = name,
            CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Memberships = { new UserTenantMembership { UserId = id, TenantId = tenant, Roles = roles } },
        });
    }

    private static string? WithDatabase(string suffix)
    {
        if (ConnectionString is null) return null;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString);
        builder.InitialCatalog += suffix;
        return builder.ConnectionString;
    }

    private static void ConfigureLocalKey(ConfigurationManager configuration)
    {
        configuration["Integration:Security:KeyEncryption:Provider"] = "Local";
        configuration["Integration:Security:LocalKeyEncryption:Current:KeyId"] = "knowledge-tests";
        configuration["Integration:Security:LocalKeyEncryption:Current:KeyVersion"] = "1";
        configuration["Integration:Security:LocalKeyEncryption:Current:KeyBase64"] =
            Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        configuration["Integration:Security:LocalKeyEncryption:AllowInProduction"] = "true";
        configuration["Integration:Security:LocalKeyEncryption:ProductionOverrideReason"] = "isolated database test host";
    }

    private static async Task<bool> FullTextInstalledAsync()
    {
        var connectionString = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'))";
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }
}

public sealed class MutableKnowledgeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
}

[CollectionDefinition("knowledge host")]
public sealed class KnowledgeHostCollection : ICollectionFixture<KnowledgeHostFixture>;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (KnowledgeHostFixture.ConnectionString is null) Skip = "YOJANA_TEST_SQL is not set";
    }
}
