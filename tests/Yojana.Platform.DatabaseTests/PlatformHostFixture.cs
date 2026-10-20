using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yojana.Platform;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Security;

namespace Yojana.Platform.DatabaseTests;

/// <summary>
/// One platform host per test run against the database named by YOJANA_TEST_SQL (created and migrated on
/// start). Tests are skipped, not failed, when the variable is absent so `make test` stays database-free.
/// </summary>
public sealed class PlatformHostFixture : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL");

    public WebApplication Host { get; private set; } = null!;

    public MutablePlatformClock Clock { get; } = new(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));

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
        builder.Configuration["Platform:ServiceName"] = "yojana-tests";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "platform-database-tests-only-hmac-key";
        builder.Configuration["Platform:SignIn:MaintenanceBatchSize"] = "2";
        builder.AddYojanaPlatform();
        builder.Services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
        Host = builder.Build();
        Host.UseYojanaPlatform();
        Host.MapYojanaSecurityEndpoints();
        await Host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await Host.StopAsync();
        await Host.DisposeAsync();
    }

    public HttpClient Client()
    {
        var address = Host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}

public sealed class MutablePlatformClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
}

[CollectionDefinition("platform host")]
public sealed class PlatformHostCollection : ICollectionFixture<PlatformHostFixture>;

/// <summary>Marks a fact that needs the database; skipped when YOJANA_TEST_SQL is not set.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (PlatformHostFixture.ConnectionString is null)
        {
            Skip = "YOJANA_TEST_SQL is not set";
        }
    }
}
