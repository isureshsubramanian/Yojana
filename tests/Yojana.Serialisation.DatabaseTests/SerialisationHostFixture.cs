using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation;
using Yojana.Serialisation.Certificates;

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

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Platform:ConnectionString"] = ConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:ServiceName"] = "yojana-serialisation-tests";
        builder.Configuration[$"{CertificateRunOptions.SectionName}:MaximumSynchronousUnits"] = "120";
        PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(SerialisationHostFixture).Assembly); // the published-event sink
        builder.AddYojanaPlatform();
        builder.Services.AddSingleton<TestCertificateRunChunkObserver>();
        builder.Services.AddSingleton<ICertificateRunChunkObserver>(sp => sp.GetRequiredService<TestCertificateRunChunkObserver>());
        builder.AddSerialisationModule();
        Host = builder.Build();
        await Host.StartAsync();
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

    public async Task<TenantId> NewTenantAsync(string prefix)
    {
        using var scope = ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = new TenantId(Guid.NewGuid());
        // Keep at least eight random characters: a prefix that filled the 20-character code alone repeated across runs.
        var stem = prefix.Length > 12 ? prefix[..12] : prefix;
        db.Tenants.Add(new Tenant { Id = id, Code = stem + Guid.NewGuid().ToString("N")[..(20 - stem.Length)], Name = "test " + prefix, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
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
