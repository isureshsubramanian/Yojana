namespace Yojana.Serialisation.Contracts;

/// <summary>Condition answer for ATP, picking and replacement order lines; callers do not inspect condition storage.</summary>
public interface IUnitConditionQueryPort
{
    Task<ConditionRequirementResult> SatisfiesAsync(Guid unitId, string requirement, CancellationToken cancellationToken);
    Task<ConditionDisclosure?> DisclosureAsync(Guid unitId, ConditionDisclosurePolicy policy, CancellationToken cancellationToken);
}

public sealed record UnitShipmentCheck(Guid UnitId, Guid ItemId, DateTime ShipmentAt);
public sealed record UnitShipmentGuardDecision(Guid UnitId, bool Allowed, string? Rule = null, string? Detail = null);

/// <summary>Set-level sale-and-shipment guard; Inventory and document posting ask it without knowing sanitisation tables.</summary>
public interface IUnitSaleShipmentGuard
{
    Task<IReadOnlyDictionary<Guid, UnitShipmentGuardDecision>> CheckAsync(IReadOnlyList<UnitShipmentCheck> units, CancellationToken cancellationToken);
}

public sealed record OpenLifeEpisode(Guid UnitId, string Kind, DateTime StartedAt, string ActorRef, Guid OpeningEventId, Guid? WorkOrderId = null, int? CycleNumber = null);
public sealed record CloseLifeEpisode(Guid LifeEpisodeId, DateTime EndedAt, Guid ClosingEventId);

/// <summary>Writer contract used by the later refurbishment aggregate; its caller supplies the ledger event ids.</summary>
public interface ILifeEpisodeWriter
{
    Task<Guid> OpenAsync(OpenLifeEpisode episode, CancellationToken cancellationToken);
    Task CloseAsync(CloseLifeEpisode episode, CancellationToken cancellationToken);
}
