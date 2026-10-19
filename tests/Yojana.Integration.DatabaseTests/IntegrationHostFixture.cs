using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Yojana.Integration;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Workers;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation;

namespace Yojana.Integration.DatabaseTests;

public sealed class IntegrationHostFixture : IAsyncLifetime
{
    public const string FakeConnectorCode = "FX01";
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL");
    public static string? ImportFilesConnectionString => WithDatabase("_files");
    public MutableTestClock Clock { get; } = new(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
    public IHost Host { get; private set; } = null!;
    public FakeReconciliationSource Poller => ReconciliationSource;
    public IServiceProvider Services => Host.Services;
    public FakeOutboundConnector Connector { get; } = new();
    public FakeReconciliationSource ReconciliationSource { get; } = new();

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Platform:ConnectionString"] = ConnectionString;
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:DatabaseConnectionString"] = ImportFilesConnectionString;
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:FileRoot"] = Path.Combine(Path.GetTempPath(), "yojana-integration-imports");
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:0:Name"] = "folder-archive";
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:0:Kind"] = "folder";
        builder.Configuration[$"{Yojana.Serialisation.Imports.ImportFileStorageOptions.SectionName}:ArchiveStores:0:Path"] = Path.Combine(Path.GetTempPath(), "yojana-integration-imports-archive");
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "yojana-integration-tests";
        builder.Configuration["Serialisation:Documents:ConfirmDeliverySeparately"] = "false";
        ConfigureLocalKey(builder.Configuration);
        PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(IntegrationModule).Assembly);
        builder.AddYojanaPlatform();
        builder.AddSerialisationModule();
        builder.AddIntegrationModule();
        builder.Services.AddSingleton(Connector);
        builder.Services.AddKeyedSingleton<IOutboundConnector>(FakeConnectorCode, (services, _) =>
            services.GetRequiredService<FakeOutboundConnector>());
        builder.Services.AddSingleton(ReconciliationSource);
        builder.Services.AddKeyedSingleton<IConnectorAdapter>(FakeConnectorCode, (services, _) =>
            services.GetRequiredService<FakeReconciliationSource>());
        builder.Services.AddSingleton<IClock>(Clock);
        builder.Services.Replace(ServiceDescriptor.Singleton<IConnectionThrottleDelay>(
            new AdvancingDatabaseThrottleDelay(Clock)));
        Host = builder.Build();
        await Host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (ConnectionString is null) return;
        await Host.StopAsync();
        Host.Dispose();
    }

    public IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    public async Task<TenantId> NewTenantAsync(string prefix)
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenants.Add(new Tenant { Id = tenant, Code = prefix + Guid.NewGuid().ToString("N")[..8], Name = "test " + prefix, CreatedAt = Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        using (var scope = ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync("serial_confirmation_mode", "copy_allowed", CancellationToken.None);
        return tenant;
    }

    public async Task<Guid> AddConnectionAsync(TenantId tenant, Guid legalEntity,
        string connectorCode = "CX01")
    {
        var id = Guid.CreateVersion7();
        using var scope = ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.Connections.Add(new Connection { TenantId = tenant, ConnectionId = id, ConnectorCode = connectorCode, DisplayLabel = "Fictional ERP", Status = ConnectionStatus.Active, CreatedAt = Clock.UtcNow });
        db.ConnectionGatewaySettings.Add(new ConnectionGatewaySetting { TenantId = tenant, ConnectionId = id, LegalEntityId = legalEntity, OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", OrderingWaitMinutes = 15 });
        foreach (var message in new[] { "ItemUpserted", "PartyUpserted", "GoodsReceiptPosted", "ProductionCompleted", "DeliveryPosted", "InvoicePosted", "ReturnReceived", "TransferPosted", "InventoryAdjusted", "WarrantyLineSold", "DocumentCancelled", "CreateReplacementDelivery", "CreateCreditMemo", "PostWarrantyExpense", "ChangeStockStatusOrLocation", "AttachDocument", "CreateServiceOrder" })
            db.ConnectionCapabilities.Add(new ConnectionCapability { TenantId = tenant, ConnectionId = id, CanonicalMessage = message, SupportLevel = CapabilitySupport.Native });
        for (var rule = 1; rule <= 7; rule++)
            db.ConnectionDegradationSettings.Add(new ConnectionDegradationSetting { TenantId = tenant, ConnectionId = id, RuleNumber = rule, Enabled = true, SettingsJson = "{}", UpdatedAt = Clock.UtcNow });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, id), new(connectorCode, new(null, null, null, 100, 1, TimeSpan.FromMinutes(5))),
            CancellationToken.None);
        return id;
    }

    private static string? WithDatabase(string suffix)
    {
        if (ConnectionString is null) return null;
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        builder.InitialCatalog += suffix;
        return builder.ConnectionString;
    }

    public static void ConfigureLocalKey(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        configuration["Integration:Security:KeyEncryption:Provider"] = "Local";
        configuration["Integration:Security:LocalKeyEncryption:Current:KeyId"] = "integration-tests";
        configuration["Integration:Security:LocalKeyEncryption:Current:KeyVersion"] = "1";
        configuration["Integration:Security:LocalKeyEncryption:Current:KeyBase64"] =
            Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        configuration["Integration:Security:LocalKeyEncryption:AllowInProduction"] = "true";
        configuration["Integration:Security:LocalKeyEncryption:ProductionOverrideReason"] = "isolated database test host";
    }
}

public sealed class MutableTestClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

internal sealed class AdvancingDatabaseThrottleDelay(MutableTestClock clock) : IConnectionThrottleDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        clock.UtcNow += delay;
        return Task.CompletedTask;
    }
}

[CollectionDefinition("integration host")]
public sealed class IntegrationHostCollection : ICollectionFixture<IntegrationHostFixture>;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (IntegrationHostFixture.ConnectionString is null) Skip = "YOJANA_TEST_SQL is not set";
    }
}
