using System.Security.Cryptography;
using System.Text;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Transport;

public sealed record IntegrationTransportRoute(string Mode, Guid? CredentialReference)
{
    public const string InProcess = "in_process";
    public const string MessageQueue = "message_queue";
    public static IntegrationTransportRoute Local { get; } = new(InProcess, null);

    public string AddressPrefix(TenantId tenant) => "yj-" + Hash(tenant.Value.ToString("N"))
        + "-" + Hash(CredentialReference?.ToString("N") ?? "local");

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}

/// <summary>The route is captured before the SQL outbox commits, never re-resolved during retries.</summary>
public sealed record DispatchIntegrationTransport(IntegrationTransportRoute Route, IntegrationTransportMessage Message);

public interface IIntegrationRouteResolver
{
    Task<IntegrationTransportRoute> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>Carrier acknowledgement only; acceptance, translation and outbound writes belong to the receiver.</summary>
public interface IIntegrationTransport
{
    Task SendAsync(IntegrationTransportRoute route, IntegrationTransportMessage message, CancellationToken cancellationToken);
    Task EnsureReceivingAsync(TenantId tenant, IntegrationTransportRoute route, CancellationToken cancellationToken);
    Task<string> ProbeAsync(TenantId tenant, IntegrationTransportRoute route, CancellationToken cancellationToken);
}

public interface IIntegrationTransportReceiver
{
    Task<IntegrationTransportReceipt> ReceiveAsync(TenantId tenant, IntegrationTransportMessage message, CancellationToken cancellationToken);
}
