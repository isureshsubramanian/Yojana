using System.Security.Cryptography;
using System.Text;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.LegacyMigration;

public sealed class MigrationSourceSecret(
    ITenantContext tenant,
    IClock clock,
    IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector)
{
    private const string Purpose = "migration_source_connection_string";

    public async Task SetAsync(Guid sourceId, string connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Length > 64000)
            throw new MigrationRuleException("migration_source_connection_string_invalid",
                "A non-empty source connection string of at most 64,000 characters is required.");
        var bytes = Encoding.UTF8.GetBytes(connectionString);
        try
        {
            var encrypted = await protector.EncryptAsync(tenant.TenantId, sourceId, Purpose, bytes, null,
                clock.UtcNow.AddDays(365), cancellationToken);
            await credentials.SaveAsync(encrypted, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<bool> IsSetAsync(Guid sourceId, CancellationToken cancellationToken) =>
        await credentials.GetCurrentAsync(tenant.TenantId, sourceId, Purpose, cancellationToken) is not null;

    public async Task<string?> GetAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var encrypted = await credentials.GetCurrentAsync(tenant.TenantId, sourceId, Purpose, cancellationToken);
        if (encrypted is null) return null;
        var bytes = await protector.DecryptAsync(encrypted, cancellationToken);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
