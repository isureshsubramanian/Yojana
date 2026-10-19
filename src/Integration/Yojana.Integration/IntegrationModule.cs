using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yojana.Integration.Inbox;
using Yojana.Integration.Gateway;
using Yojana.Serialisation.Contracts;
using Yojana.Integration.Ordering;
using Yojana.Platform.Scheduling;
using Yojana.Integration.Data;
using Yojana.Platform;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Credentials;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Webhooks;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration;

/// <summary>Module registration. Owns the <c>integration</c> schema through <see cref="IntegrationDbContext"/>.</summary>
public static class IntegrationModule
{
    public const string Schema = IntegrationDbContext.SchemaName;

    public static IHostApplicationBuilder AddIntegrationModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<IntegrationDbContext>(Schema);
        builder.Services.AddScoped<IdempotencyIndex>();
        builder.Services.AddScoped<InboundMessageStore>();
        builder.Services.AddSingleton<IChangeMarkerComparer, ChangeMarkerComparer>();
        builder.Services.AddScoped<GatewayReferenceStore>();
        builder.Services.AddScoped<ConnectionCapabilityReader>();
        builder.Services.AddScoped<InboundGateway>();
        // The published-event chains already contain Serialisation handlers with their own EF transaction. Resolve the
        // staging service through a factory so Wolverine does not infer IntegrationDbContext as a second transaction
        // owner for that same public message. The stager owns and saves its narrow staging unit of work explicitly.
        builder.Services.AddScoped(sp => new OutboundCommandStager(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>(),
            sp.GetRequiredService<ConnectionCapabilityReader>()));
        builder.Services.AddScoped<BoundedWaitResequencer>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ResequencerJob>(ResequencerJob.JobName, ResequencerJob.Cron));
        // Factories deliberately hide IntegrationDbContext from Wolverine's SerialisationDbContext handler graphs.
        // The document and item ports are module contracts, but exposing both contexts as constructor-discoverable
        // dependencies makes Wolverine reject the otherwise loosely coupled handler at startup.
        builder.Services.AddScoped(sp => new IntegrationDocumentQueryPort(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>()));
        builder.Services.AddScoped<IPurchasingDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped<IManufacturingDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped<IInventoryDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped<ISalesDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped(sp => new IntegrationItemQueryPort(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>()));
        builder.Services.AddScoped<IItemQueryPort>(sp => sp.GetRequiredService<IntegrationItemQueryPort>());

        builder.AddIntegrationSecurity();
        return builder;
    }

    /// <summary>Security services (P8-T14): key encryption, credential protection, audit trail, webhook verification and
    /// per-connection worker lanes. Separate from the database registration so it can be composed and tested alone.</summary>
    public static IHostApplicationBuilder AddIntegrationSecurity(this IHostApplicationBuilder builder)
    {
        var keyProvider = builder.Configuration["Integration:Security:KeyEncryption:Provider"];
        if (StringComparer.OrdinalIgnoreCase.Equals(keyProvider, "Local"))
        {
            var options = LocalKeyEncryptionOptions.From(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider, LocalKeyEncryptionKeyProvider>();
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(keyProvider, "AzureKeyVault"))
        {
            var options = AzureKeyVaultKeyEncryptionOptions.From(builder.Configuration);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(AzureCredentialFactory.Create(options));
            builder.Services.TryAddSingleton<IAzureKeyVaultCryptography>(services =>
                new AzureSdkKeyVaultCryptography(options,
                    services.GetRequiredService<Azure.Core.TokenCredential>()));
            builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider>(services =>
                new AzureKeyVaultKeyEncryptionKeyProvider(options,
                    services.GetRequiredService<IAzureKeyVaultCryptography>()));
        }
        else if (!string.IsNullOrWhiteSpace(keyProvider))
            throw new InvalidOperationException(
                "Integration:Security:KeyEncryption:Provider must be Local or AzureKeyVault.");

        builder.Services.TryAddSingleton<InMemoryTenantDataKeyStore>();
        builder.Services.TryAddSingleton<ITenantDataKeyStore>(services =>
            services.GetRequiredService<InMemoryTenantDataKeyStore>());
        builder.Services.TryAddSingleton<ITenantDataKeyRewrapStore>(services =>
            services.GetRequiredService<InMemoryTenantDataKeyStore>());
        builder.Services.TryAddSingleton<ITenantDataKeyRewrapCheckpointStore,
            InMemoryTenantDataKeyRewrapCheckpointStore>();
        builder.Services.TryAddSingleton<IConnectionCredentialStore, InMemoryConnectionCredentialStore>();
        builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider, MissingKeyEncryptionKeyProvider>();

        var auditProvider = builder.Configuration["Integration:Security:Audit:Provider"];
        if (StringComparer.OrdinalIgnoreCase.Equals(auditProvider, "LocalFile"))
        {
            var root = builder.Configuration["Integration:Security:Audit:LocalRoot"];
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException(
                    "Integration:Security:Audit:LocalRoot is required when the LocalFile audit provider is selected.");
            builder.Services.AddSingleton(new LocalConnectionAuditOptions(root));
            builder.Services.TryAddSingleton<IConnectionAuditSink, LocalAppendOnlyConnectionAuditSink>();
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(auditProvider, "AzureBlob"))
        {
            var options = AzureBlobConnectionAuditOptions.From(builder.Configuration);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(services =>
            {
                if (!string.IsNullOrWhiteSpace(options.ConnectionString))
                    return new BlobServiceClient(options.ConnectionString);
                var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = options.ManagedIdentityClientId,
                    ExcludeInteractiveBrowserCredential = true,
                    ExcludeVisualStudioCredential = true,
                    ExcludeVisualStudioCodeCredential = true,
                    ExcludeAzureCliCredential = true,
                    ExcludeAzurePowerShellCredential = true,
                    ExcludeAzureDeveloperCliCredential = true,
                    ExcludeBrokerCredential = true,
                });
                return new BlobServiceClient(options.ServiceUri!, credential);
            });
            builder.Services.TryAddSingleton(services =>
                services.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(options.ContainerName));
            builder.Services.TryAddSingleton<IAppendBlobStore, AzureAppendBlobStore>();
            builder.Services.TryAddSingleton<IConnectionAuditSink>(services =>
                new AzureBlobConnectionAuditSink(services.GetRequiredService<IAppendBlobStore>()));
        }
        else if (!string.IsNullOrWhiteSpace(auditProvider))
            throw new InvalidOperationException(
                "Integration:Security:Audit:Provider must be LocalFile or AzureBlob.");

        builder.Services.TryAddScoped<TenantDataKeyService>();
        builder.Services.TryAddScoped<TenantDataKeyRewrapService>();
        builder.Services.TryAddScoped<ConnectionCredentialProtector>();
        builder.Services.TryAddScoped<CredentialExpiryAlertEvaluator>();
        builder.Services.TryAddSingleton<HmacSha256WebhookSignatureVerifier>();
        builder.Services.TryAddSingleton<IConnectionWorkerIsolation, ConnectionWorkerIsolation>();
        builder.Services.TryAddSingleton<InMemoryConnectionThrottleProfileStore>();
        builder.Services.TryAddSingleton<IConnectionThrottleProfileStore>(services =>
            services.GetRequiredService<InMemoryConnectionThrottleProfileStore>());
        builder.Services.TryAddSingleton<InMemoryConnectionDailyBudgetStore>();
        builder.Services.TryAddSingleton<IConnectionDailyBudgetStore>(services =>
            services.GetRequiredService<InMemoryConnectionDailyBudgetStore>());
        builder.Services.TryAddSingleton<IConnectionCallBudgetAlertPublisher,
            NullConnectionCallBudgetAlertPublisher>();
        builder.Services.TryAddSingleton<IConnectionThrottleDelay, SystemConnectionThrottleDelay>();
        builder.Services.TryAddSingleton<IConnectionRetryJitter, RandomConnectionRetryJitter>();
        builder.Services.TryAddSingleton<IConnectionThrottleWorkerRegistry,
            ConnectionThrottleWorkerRegistry>();
        builder.Services.TryAddScoped<ConnectionThrottleProfiles>();
        return builder;
    }
}
