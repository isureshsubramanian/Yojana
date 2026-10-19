namespace Yojana.Integration.Contracts;

public sealed record IntegrationTransportHealth(string Mode, string State, DateTime CheckedAt);

/// <summary>A tenant-scoped health read; connection projections may consume it without owning the transport.</summary>
public interface IIntegrationTransportHealth
{
    Task<IntegrationTransportHealth> ReadAsync(CancellationToken cancellationToken);
}
