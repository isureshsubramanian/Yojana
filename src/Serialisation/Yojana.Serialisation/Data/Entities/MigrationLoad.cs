using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// One unit written by a migration load (P9-T10, OD-52, OD-53): the register that makes a resent chunk a no-op and
/// tells rollback exactly which rows a load owns. <see cref="EventCount"/> is the number of ledger facts the load
/// wrote for the unit; a unit with more facts than that was touched after the load and is never rolled back.
/// </summary>
public sealed class MigrationLoadUnit : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid LoadId { get; set; }

    public Guid PlanUnitId { get; set; }

    public Guid UnitId { get; set; }

    public Guid CreatedEventId { get; set; }

    public DateTime CreatedEventTime { get; set; }

    public int EventCount { get; set; }

    public DateTime AppliedAt { get; set; }

    public required string ActorRef { get; set; }
}
