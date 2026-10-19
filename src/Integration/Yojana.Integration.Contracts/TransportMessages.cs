namespace Yojana.Integration.Contracts;

public sealed record IntegrationTransportInbound(CanonicalEnvelope Envelope, string MessageType,
    string CanonicalMessage, string RawPayload);

public sealed record IntegrationTransportMessage(Guid TenantId, Guid DeliveryId,
    IntegrationTransportInbound? Inbound = null, Guid? RelayConnectionId = null);

public sealed record IntegrationOutboundRelayReceipt(int Completed, int Retrying, int Parked);

public sealed record IntegrationTransportReceipt(Guid DeliveryId, string Outcome, Guid? InboundMessageId = null,
    IntegrationOutboundRelayReceipt? Relay = null);
