namespace Yojana.Serialisation.Contracts;

// Migration load (P9-T10, OD-52, OD-53): how a reviewed load plan enters this module. The caller (Integration's
// migration apply step) resolves every reference first: items, locations and parties arrive as module ids, never as
// source codes. One message carries one bounded chunk of units and is written set-based in one transaction; a unit
// already written by the same load comes back as already applied, so a chunk can be resent after a failure.
// Migrated history fires no per-unit published events (OD-52): the apply step publishes one MigrationLoadApplied.

/// <summary>
/// Write one chunk of migrated units with identifiers, history, current custody and ownership, reviewed cover,
/// replacement links and one lazy-rendered certificate record per unit.
/// </summary>
public sealed record ApplyMigratedUnits(
    Guid LoadId,
    Guid LegalEntityId,
    string ActorRef,
    string Source,
    IReadOnlyList<MigratedUnit> Units,
    DateOnly? CutoverDate = null);

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
    string? ConditionProvenance = null,
    IReadOnlyList<MigratedEntitlement>? Entitlements = null,
    IReadOnlyList<MigratedReplacementLink>? ReplacementLinks = null,
    MigratedCertificate? Certificate = null);

/// <summary>
/// A plan entitlement names the exact published policy version, but its dates remain source evidence: migration never
/// recalculates, caps or floors them from that policy. Evidence contains every source row considered, with the selected
/// row marked, so a later reviewer can see both the result and the alternatives.
/// </summary>
public sealed record MigratedEntitlement(
    Guid PlanEntitlementId,
    string CoverageFamily,
    Guid PolicyVersionId,
    int PolicyVersion,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateOnly? TerminationDate,
    string Direction,
    string DateSource,
    string SourceKey,
    IReadOnlyList<MigratedWarrantyEvidence> Evidence);

public sealed record MigratedWarrantyEvidence(
    string SourceKind,
    string SourceKey,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateOnly? TerminationDate,
    string Direction,
    bool Selected);

/// <summary>A historical replacement pair. The carrier is chosen by the caller; both plan ids remain explicit.</summary>
public sealed record MigratedReplacementLink(
    Guid PlanReplacementLinkId,
    Guid ReplacedPlanUnitId,
    Guid ReplacementPlanUnitId,
    string PolicyApplied,
    string SourceKey,
    string Direction,
    string ReplacementReason,
    string State,
    string ReplacementConditionAllowed,
    string PricePolicy,
    decimal DifferenceAmount,
    string Currency);

/// <summary>Version-one release record reconstructed from the plan; rendering stays lazy.</summary>
public sealed record MigratedCertificate(
    Guid PlanCertificateId,
    int Version,
    string Provenance,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Evidence);

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
/// <see cref="Delivery"/> is the incumbent's delivery to that customer when the plan has one: the start evidence a later
/// <c>BackfillEntitlements</c> uses for this unit (P9-T09), since a migrated ownership row names no module document.
/// </summary>
public sealed record MigratedOwnership(Guid PartyRef, string PartyType, DateTime From, bool Pseudonymised,
    Guid? DirectCustomerPartyRef = null, MigratedDelivery? Delivery = null);

/// <summary>The incumbent's delivery document, as the plan read it: when it was delivered and the source's label and number.</summary>
public sealed record MigratedDelivery(DateTime At, string? DocumentLabel, string? DocumentNumber);

public sealed record MigratedUnitOutcome(Guid PlanUnitId, Guid? UnitId, string Outcome, IReadOnlyList<string> Reasons)
{
    public const string Applied = "applied";
    public const string AlreadyApplied = "already_applied";
    public const string Refused = "refused";
}

public sealed record ApplyMigratedUnitsResult(IReadOnlyList<MigratedUnitOutcome> Units, int Applied, int AlreadyApplied,
    int Refused, int EventsWritten, int EntitlementsWritten = 0, int ReplacementLinksWritten = 0,
    int CertificatesRecorded = 0);

/// <summary>
/// Undo a rehearsal load (OD-53): every row the load wrote is removed. Refused as a whole when any unit of the load
/// was touched after it (a later fact in the ledger); the touched units are listed so a person can decide.
/// </summary>
public sealed record RollBackMigrationLoad(Guid LoadId, string ActorRef);

public sealed record RollBackMigrationLoadResult(Guid LoadId, string Outcome, int UnitsRemoved, int EventsRemoved,
    IReadOnlyList<Guid> TouchedUnitIds, int EntitlementsRemoved = 0, int ReplacementLinksRemoved = 0,
    int CertificatesRemoved = 0, int TouchedUnitCount = 0)
{
    public const string RolledBack = "rolled_back";
    public const string RefusedTouched = "refused_touched";
    public const string NothingToRemove = "nothing_to_remove";
}

/// <summary>Units of a load: the plan unit, the module unit and when it was written. Read by validation and rollback.</summary>
public sealed record MigrationLoadUnitsQuery(Guid LoadId, Guid? AfterPlanUnitId = null, int Take = 1000);

public sealed record MigrationLoadUnitRow(Guid PlanUnitId, Guid UnitId, DateTime AppliedAt);

public sealed record MigrationLoadUnitsPage(IReadOnlyList<MigrationLoadUnitRow> Items, Guid? NextAfterPlanUnitId);

/// <summary>
/// Units that prevent a rehearsal rollback because later facts were written. This is a separate, keyset-paged read
/// so a large touched load never makes the rollback command response unbounded.
/// </summary>
public sealed record TouchedMigrationLoadUnitsQuery(Guid LoadId, Guid? AfterUnitId = null, int Take = 50);

public sealed record TouchedMigrationLoadUnitRow(Guid UnitId, string Serial);

public sealed record TouchedMigrationLoadUnitsPage(IReadOnlyList<TouchedMigrationLoadUnitRow> Items, int Total,
    Guid? NextAfterUnitId);

/// <summary>One summary fact per applied load (OD-52): the only event migrated history publishes.</summary>
public sealed record MigrationLoadApplied(Guid LoadId, Guid BatchId, bool Rehearsal, int Units, int Events,
    DateTime AppliedAt, int Entitlements = 0, int ReplacementLinks = 0, int Certificates = 0);
