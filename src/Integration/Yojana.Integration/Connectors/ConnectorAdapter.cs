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

public sealed class ConnectorAdapterResolver(IEnumerable<IConnectorAdapter> adapters) : IConnectorAdapterResolver
{
    private readonly Dictionary<string, IConnectorAdapter> byCode = adapters.ToDictionary(
        adapter => adapter.ConnectorCode, StringComparer.Ordinal);

    public IConnectorPoller Poller(string connectorCode) => Resolve(connectorCode);

    public IConnectorReconciliationSource ReconciliationSource(string connectorCode) => Resolve(connectorCode);

    private IConnectorAdapter Resolve(string connectorCode) => byCode.TryGetValue(connectorCode, out var adapter)
        ? adapter
        : throw new InvalidOperationException($"No connector adapter is registered for code '{connectorCode}'.");
}
