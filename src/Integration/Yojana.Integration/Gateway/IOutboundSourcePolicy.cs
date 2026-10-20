namespace Yojana.Integration.Gateway;

/// <summary>Connection policy for using recorded delivery sites when a new document has no explicit destination.</summary>
public interface IOutboundSourcePolicy
{
    string ConnectorCode { get; }
    Task<bool> UseRecordedReplacementShipToAsync(Guid connectionId, CancellationToken cancellationToken);
}
