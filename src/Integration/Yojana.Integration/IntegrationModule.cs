using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Credentials;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Webhooks;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration;

/// <summary>Module registration. Owns the <c>integration</c> schema; its DbContext and migrations arrive in P8.</summary>
public static class IntegrationModule
{
    public const string Schema = "integration";

    public static IHostApplicationBuilder AddIntegrationModule(this IHostApplicationBuilder builder)
    {
        var keyProvider = builder.Configuration["Integration:Security:KeyEncryption:Provider"];
        if (StringComparer.OrdinalIgnoreCase.Equals(keyProvider, "Local"))
        {
            var options = LocalKeyEncryptionOptions.From(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider, LocalKeyEncryptionKeyProvider>();
            builder.Services.TryAddSingleton<ITenantDataKeyStore, InMemoryTenantDataKeyStore>();
            builder.Services.TryAddSingleton<IConnectionCredentialStore, InMemoryConnectionCredentialStore>();
        }

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

        builder.Services.TryAddScoped<TenantDataKeyService>();
        builder.Services.TryAddScoped<ConnectionCredentialProtector>();
        builder.Services.TryAddScoped<CredentialExpiryAlertEvaluator>();
        builder.Services.TryAddSingleton<HmacSha256WebhookSignatureVerifier>();
        builder.Services.TryAddSingleton<IConnectionWorkerIsolation, ConnectionWorkerIsolation>();
        return builder;
    }
}
