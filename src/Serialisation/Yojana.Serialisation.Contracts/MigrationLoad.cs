namespace Yojana.Serialisation.Contracts;

// Migration load (P9-T10, OD-52, OD-53): how a reviewed load plan enters this module. The caller (Integration's
// migration apply step) resolves every reference first: items, locations and parties arrive as module ids, never as
// source codes. One message carries one bounded chunk of units and is written set-based in one transaction; a unit
// already written by the same load comes back as already applied, so a chunk can be resent after a failure.
// Migrated history fires no per-unit published events (OD-52): the apply step publishes one MigrationLoadApplied.

/// <summary>Write one chunk of migrated units: units, identifiers, history, current custody and ownership.</summary>
public sealed record ApplyMigratedUnits(
    Guid LoadId,
    Guid LegalEntityId,
    string ActorRef,
    string Source,
    IReadOnlyList<MigratedUnit> Units);

/// <summary>
/// One unit from the load plan. <see cref="CreatedAt"/> is the business time of the creating fact (the first
/// history event, or the opening balance); every history event, custody and ownership row is at or after it.
/// </summary>
public sealed record MigratedUnit(
    Guid PlanUnitId,
    Guid ItemId,
    string HistoryDepth,
    DateTime CreatedAt,
    IReadOnlyList<IdentifierInput> Identifiers,
    IReadOnlyList<MigratedHistoryEvent> History,
    MigratedCustody? Custody,
    MigratedOwnership? Ownership,
    string? ConditionGrade,
    string? ConditionProvenance = null);

/// <summary>
/// One reconstructed movement as the source recorded it. Kept as a state-neutral ledger fact
/// (<see cref="UnitEventType.MigratedHistory"/>); current state comes from <see cref="MigratedUnit.Custody"/> and
/// <see cref="MigratedUnit.Ownership"/>, never from replaying source movements.
/// </summary>
public sealed record MigratedHistoryEvent(
    DateTime At,
    string MovementKind,
    string Provenance,
    string? DocumentLabel,
    int? DocumentNumber,
    string? PartyCode,
    string? WarehouseCode,
    decimal Quantity);

/// <summary>The unit is in our hands at a location (in stock in the source at cutover).</summary>
public sealed record MigratedCustody(Guid LocationId, string LocationKind, DateTime From);

/// <summary>
/// The current owner. A pseudonymised party carries a reference with no name behind it. The direct customer of a
/// two-tier sale (the reseller) is kept as evidence on the ownership fact; the one open ownership row names the end customer.
/// </summary>
public sealed record MigratedOwnership(Guid PartyRef, string PartyType, DateTime From, bool Pseudonymised,
    Guid? DirectCustomerPartyRef = null);

public sealed record MigratedUnitOutcome(Guid PlanUnitId, Guid? UnitId, string Outcome, IReadOnlyList<string> Reasons)
{
    public const string Applied = "applied";
    public const string AlreadyApplied = "already_applied";
    public const string Refused = "refused";
}

public sealed record ApplyMigratedUnitsResult(IReadOnlyList<MigratedUnitOutcome> Units, int Applied, int AlreadyApplied,
    int Refused, int EventsWritten);

/// <summary>
/// Undo a rehearsal load (OD-53): every row the load wrote is removed. Refused as a whole when any unit of the load
/// was touched after it (a later fact in the ledger); the touched units are listed so a person can decide.
/// </summary>
public sealed record RollBackMigrationLoad(Guid LoadId, string ActorRef);

public sealed record RollBackMigrationLoadResult(Guid LoadId, string Outcome, int UnitsRemoved, int EventsRemoved,
    IReadOnlyList<Guid> TouchedUnitIds)
{
    public const string RolledBack = "rolled_back";
    public const string RefusedTouched = "refused_touched";
    public const string NothingToRemove = "nothing_to_remove";
}

/// <summary>Units of a load: the plan unit, the module unit and when it was written. Read by validation and rollback.</summary>
public sealed record MigrationLoadUnitsQuery(Guid LoadId, Guid? AfterPlanUnitId = null, int Take = 1000);

public sealed record MigrationLoadUnitRow(Guid PlanUnitId, Guid UnitId, DateTime AppliedAt);

public sealed record MigrationLoadUnitsPage(IReadOnlyList<MigrationLoadUnitRow> Items, Guid? NextAfterPlanUnitId);

/// <summary>One summary fact per applied load (OD-52): the only event migrated history publishes.</summary>
public sealed record MigrationLoadApplied(Guid LoadId, Guid BatchId, bool Rehearsal, int Units, int Events,
    DateTime AppliedAt);
