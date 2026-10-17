namespace Yojana.Serialisation.Contracts;

/// <summary>Immutable coverage history for one unit, ordered newest first by the read endpoint.</summary>
public sealed record CoverageDecisionRow(
    Guid CoverageDecisionId,
    Guid UnitId,
    string Reason,
    string WorkType,
    Guid WorkId,
    Guid? WorkLineId,
    CoverageDecisionSnapshot Before,
    CoverageDecisionSnapshot After,
    Guid SourceEventId,
    DateTime DecidedAt,
    string ActorRef);

public sealed record CoverageDecisionHistory(IReadOnlyList<CoverageDecisionRow> Items);

/// <summary>Tenant policy for the minimum cover given to serialised parts fitted during customer repair.</summary>
public sealed record ServicePartEntitlementPolicy(int DefaultMinimumDays = 90, IReadOnlyDictionary<Guid, int>? ItemMinimumDays = null);

/// <summary>The Service module's claim-like inputs for one open order line.</summary>
public sealed record OpenServiceOrderCoverage(
    Guid ServiceOrderId,
    Guid? ServiceOrderLineId,
    Guid UnitId,
    Guid CustomerPartyRef,
    Guid ObligorEntityId,
    DateOnly CoverageDate,
    string ServiceCountry,
    string CoverageFamily,
    string LineKind,
    decimal RequestedAmount,
    IReadOnlyDictionary<string, decimal> Measurements,
    CoverageDecisionSnapshot CurrentDecision,
    Guid? CoverageDecisionRef);

/// <summary>
/// Narrow Service seam for entitlement-driven re-adjudication. The default implementation returns no work because the
/// Service module does not exist yet; its future implementation owns how the current decision reference is persisted.
/// </summary>
public interface IServiceOrderQueryPort
{
    Task<IReadOnlyList<OpenServiceOrderCoverage>> OpenForUnitAsync(Guid unitId, CancellationToken cancellationToken);

    Task UpdateCoverageDecisionReferenceAsync(Guid serviceOrderId, Guid? serviceOrderLineId, Guid coverageDecisionId,
        CoverageDecisionSnapshot decision, CancellationToken cancellationToken);
}
