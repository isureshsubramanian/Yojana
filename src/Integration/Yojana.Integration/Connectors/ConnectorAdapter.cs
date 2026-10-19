using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Notifications;
using Yojana.Integration.Reconciliation;

namespace Yojana.Integration.Connectors;

/// <summary>
/// Registration contract for one connector adapter and its shared transport/session. Polling and reconciliation
/// remain independently callable ports: the shared resolver returns only the requested facet, and sweep/backfill
/// never require a poll or a poll watermark. A connector implements this interface once so both facets use the same
/// authentication, remote transport and connection-specific resolution without either workflow calling the other.
/// </summary>
public interface IConnectorAdapter : IConnectorPoller, IConnectorReconciliationSource;

public interface IConnectorAdapterResolver
{
    IConnectorPoller Poller(string connectorCode);
    IConnectorReconciliationSource ReconciliationSource(string connectorCode);
}

public sealed class ConnectorAdapterResolver(IServiceProvider services) : IConnectorAdapterResolver
{
    public IConnectorPoller Poller(string connectorCode) => Resolve(connectorCode);

    public IConnectorReconciliationSource ReconciliationSource(string connectorCode) => Resolve(connectorCode);

    private IConnectorAdapter Resolve(string connectorCode) =>
        services.GetRequiredKeyedService<IConnectorAdapter>(connectorCode);
}
