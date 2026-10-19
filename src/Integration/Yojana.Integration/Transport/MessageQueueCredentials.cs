using System.Security.Cryptography;
using System.Text;
using Azure.Messaging.ServiceBus;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Transport;

public sealed record StoreMessageQueueCredential(string ConnectionString, DateTime RotationDueAt)
{
    public override string ToString() => "StoreMessageQueueCredential { ConnectionString = [redacted] }";
}
public sealed record MessageQueueCredentialReference(Guid CredentialReference);

/// <summary>Reuses the envelope-encrypted credential store. References are immutable and retained while routes drain.</summary>
public sealed class MessageQueueCredentials(ITenantContext tenant, IConnectionCredentialStore store,
    ConnectionCredentialProtector protector, IClock clock)
{
    public const string Purpose = "integration_message_queue";

    public async Task<MessageQueueCredentialReference> StoreAsync(StoreMessageQueueCredential request,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = ServiceBusConnectionStringProperties.Parse(request.ConnectionString);
            if (!string.IsNullOrWhiteSpace(properties.EntityPath))
                throw new ArgumentException("Use a namespace credential with access to the tenant's topic and queue.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new ArgumentException("A valid namespace connection string is required for the message queue transport.");
        }
        if (request.RotationDueAt <= clock.UtcNow)
            throw new ArgumentException("Credential rotation must be due in the future.");
        var reference = Guid.CreateVersion7();
        var bytes = Encoding.UTF8.GetBytes(request.ConnectionString);
        try
        {
            var encrypted = await protector.EncryptAsync(tenant.TenantId, reference, Purpose, bytes,
                null, request.RotationDueAt, cancellationToken);
            await store.SaveAsync(encrypted, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return new(reference);
    }

    public async Task<bool> ExistsAsync(Guid reference, CancellationToken cancellationToken) =>
        await store.GetCurrentAsync(tenant.TenantId, reference, Purpose, cancellationToken) is not null;

    public async Task<string?> ReadSecretAsync(Guid reference, CancellationToken cancellationToken)
    {
        var credential = await store.GetCurrentAsync(tenant.TenantId, reference, Purpose, cancellationToken);
        if (credential is null) return null;
        var bytes = await protector.DecryptAsync(credential, cancellationToken);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
