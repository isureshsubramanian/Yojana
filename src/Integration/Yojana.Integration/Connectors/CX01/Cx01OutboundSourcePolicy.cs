using Yojana.Integration.Gateway;

namespace Yojana.Integration.Connectors.CX01;

public sealed class Cx01OutboundSourcePolicy(Cx01Settings settings) : IOutboundSourcePolicy
{
    public string ConnectorCode => Cx01Capabilities.ConnectorCode;
    public async Task<bool> UseRecordedReplacementShipToAsync(Guid connectionId, CancellationToken cancellationToken) =>
        (await settings.GetAsync(connectionId, cancellationToken)).Documents?.UseRecordedReplacementShipTo ?? true;
}
