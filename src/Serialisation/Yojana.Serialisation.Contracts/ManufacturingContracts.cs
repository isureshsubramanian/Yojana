namespace Yojana.Serialisation.Contracts;

/// <summary>
/// Manufacturing publishes this request after deciding to reduce a production order. The tenant is taken from the
/// transport envelope, not from the payload. Serialisation processes one payload per
/// <see cref="ThinEnvelope.MessageId"/>; a replay returns the recorded outcome and a different payload under the same
/// message id is refused. <paramref name="SurplusUnitIds"/> is optional: when absent, Serialisation chooses units that
/// have no external document, newest allocation first; when present, it is the complete surplus list and its count must
/// equal <c>OldQuantity - NewQuantity</c>. Manufacturing waits for <see cref="UnitProductionOrderReconciled"/> and the
/// corresponding <see cref="UnitVoided"/> facts before applying the quantity change.
/// </summary>
public sealed record ManufacturingProductionOrderQuantityChangeRequested(
    ThinEnvelope Envelope,
    DocumentRef ProductionOrder,
    int OldQuantity,
    int NewQuantity,
    IReadOnlyList<Guid>? SurplusUnitIds = null)
{
    public const string Schema = "Manufacturing.ProductionOrderQuantityChangeRequested";
    public const string Version = "1.0";
}

/// <summary>One open assembly link that Manufacturing has physically reverse-issued.</summary>
public sealed record ManufacturingReturnedComponent(Guid ParentUnitId, Guid AssemblyLinkId);

/// <summary>
/// Manufacturing publishes this fact after components have physically returned to the named warehouse. The tenant is
/// taken from the transport envelope. Serialisation closes each named open assembly link with reason
/// <c>reverse_issue</c> through <see cref="RecordDisassembly"/>, then moves each serialised component's custody to the
/// warehouse. Processing is idempotent on <see cref="ThinEnvelope.MessageId"/>; domain failures are returned in
/// <see cref="ManufacturingComponentReturnedResult.Refusals"/> and infrastructure failures are retried by the bus.
/// </summary>
public sealed record ManufacturingComponentReturned(
    ThinEnvelope Envelope,
    DocumentRef ProductionOrder,
    Guid WarehouseLocationId,
    IReadOnlyList<ManufacturingReturnedComponent> Components)
{
    public const string Schema = "Manufacturing.ComponentReturned";
    public const string Version = "1.0";
}

/// <summary>A named contract failure; null ids mean the request as a whole was refused.</summary>
public sealed record ManufacturingContractRefusal(
    Guid? UnitId,
    Guid? AssemblyLinkId,
    string Rule,
    string Message,
    string? UnblockingStep = null);

public sealed record ManufacturingComponentReturnedResult(
    Guid MessageId,
    DocumentRef ProductionOrder,
    IReadOnlyList<Guid> ClosedAssemblyLinkIds,
    IReadOnlyList<Guid> ReturnedUnitIds,
    IReadOnlyList<ManufacturingContractRefusal> Refusals,
    bool Duplicate = false);

public sealed record ProductionOrderQuantityChangeResult(
    Guid MessageId,
    DocumentRef ProductionOrder,
    int OldQuantity,
    int NewQuantity,
    IReadOnlyList<Guid> SelectedUnitIds,
    IReadOnlyList<Guid> VoidedUnitIds,
    IReadOnlyList<ManufacturingContractRefusal> Refusals,
    bool CanApplyQuantityChange,
    bool Duplicate = false);

/// <summary>
/// Serialisation's durable reply to one quantity-change request. <see cref="CanApplyQuantityChange"/> is true only when
/// every selected unit was voided (or no allocated surplus unit exists). Manufacturing correlates by
/// <see cref="RequestMessageId"/> and must not infer success from message ordering alone.
/// </summary>
public sealed record UnitProductionOrderReconciled(
    PublishedSummaryHeader Header,
    Guid RequestMessageId,
    DocumentRef ProductionOrder,
    int OldQuantity,
    int NewQuantity,
    IReadOnlyList<Guid> SelectedUnitIds,
    IReadOnlyList<Guid> VoidedUnitIds,
    IReadOnlyList<ManufacturingContractRefusal> Refusals,
    bool CanApplyQuantityChange) : IPublishedSummaryEvent
{
    public const string Schema = "Unit.ProductionOrderReconciled";
    public const string Version = "1.0";
}
