namespace Yojana.Serialisation.Contracts;

// Thin events the Serialisation module consumes (ADR-0001 "Integration boundaries and coupling"; ADR-0002 canonical
// contract). They carry ids and the envelope facts, never document lines: the handler fetches the document through the
// publisher's query port, so the message stays small, replayable and free of the publisher's model. Published by the
// integration gateway (P8) from canonical events, or by in-house modules (P12).

/// <summary>Envelope facts every thin event carries (ADR-0002 envelope, reduced to what the module needs).</summary>
public sealed record ThinEnvelope(
    Guid MessageId,
    Guid LegalEntityId,
    DateTime OccurredAt,
    string Source,
    string AttributionCompleteness = "complete",
    Guid? ConnectionId = null,
    string? ConnectorCode = null,
    string CaptureMode = "webhook")
{
    public static class Completeness
    {
        public const string Complete = "complete";
        public const string Partial = "partial";
        public const string Unknown = "unknown";
    }
}

public sealed record PurchasingReceiptPosted(ThinEnvelope Envelope, DocumentRef Document);

public sealed record PurchasingAsnReceived(ThinEnvelope Envelope, DocumentRef Document);

/// <summary>Drop-ship: the vendor shipped straight to the customer (case 26).</summary>
public sealed record PurchasingVendorShipConfirmed(ThinEnvelope Envelope, DocumentRef Document);

public sealed record ManufacturingProductionReceiptPosted(ThinEnvelope Envelope, DocumentRef Document);

public sealed record ManufacturingComponentIssued(ThinEnvelope Envelope, DocumentRef Document);

public sealed record InventoryTransferPosted(ThinEnvelope Envelope, DocumentRef Document);

public sealed record InventoryAdjustmentPosted(ThinEnvelope Envelope, DocumentRef Document);

public sealed record SalesShipmentPosted(ThinEnvelope Envelope, DocumentRef Document);

public sealed record SalesDeliveryConfirmed(ThinEnvelope Envelope, DocumentRef Document);

/// <summary>Custody to quarantine only in P1; RMA matching and ownership effects arrive with P5.</summary>
public sealed record SalesReturnPosted(ThinEnvelope Envelope, DocumentRef Document);

public sealed record ServiceLoanerIssued(ThinEnvelope Envelope, DocumentRef Document);

/// <summary>Finance closed a period (<c>yyyy-MM</c>): later facts dated inside it post to the first open period (case 33).</summary>
public sealed record FinancePeriodClosed(ThinEnvelope Envelope, string Period);

/// <summary>Outcome of one consumed event: what the module did with each line, for the gateway's reconciliation and the operator.</summary>
public sealed record ConsumedEventResult(string EventType, DocumentRef? Document, int UnitsAffected, int LinesPending, IReadOnlyList<string> Exceptions, bool Duplicate = false);
