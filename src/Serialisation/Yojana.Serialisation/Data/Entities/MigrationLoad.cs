using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// One unit written by a migration load (P9-T10, OD-52, OD-53): the register that makes a resent chunk a no-op and
/// tells rollback exactly which rows a load owns. <see cref="EventCount"/> is the number of ledger facts the load
/// wrote for the unit; touched checks add every later <see cref="MigrationCoverReplacement.EventsWritten"/> before
/// comparing the register total with the live and archived ledgers.
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

/// <summary>
/// One delta-load replacement of a migrated unit's cover. The closed-entitlement snapshot is the rollback boundary:
/// it records the exact pre-replacement state and the closing fact to remove without reaching into another load.
/// </summary>
public sealed class MigrationCoverReplacement : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid LoadId { get; set; }

    public Guid UnitId { get; set; }

    public Guid PlanUnitId { get; set; }

    /// <summary>JSON array of <see cref="MigrationClosedEntitlementSnapshot"/>.</summary>
    public string ClosedEntitlements { get; set; } = "[]";

    public int EventsWritten { get; set; }

    public DateTime ReplacedAt { get; set; }

    public required string ActorRef { get; set; }
}

public sealed record MigrationClosedEntitlementSnapshot(Guid EntitlementId, Guid ClosedEventId,
    string PreviousStatus, string? PreviousClosedReason, DateOnly? PreviousClosedEffective,
    DateTime? PreviousClosedAt);
