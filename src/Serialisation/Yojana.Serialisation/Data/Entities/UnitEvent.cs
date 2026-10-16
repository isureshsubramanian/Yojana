using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// Columns shared by the live ledger and its archive (ADR-0001 "UnitEvent (the timeline)"). Both tables are
/// range-partitioned monthly on <see cref="EventTime"/> on the same partition scheme, so a cold partition
/// switches from one to the other as a metadata operation. Rows are never updated or deleted.
/// </summary>
public abstract class UnitEventRow : ITenantScoped
{
    public TenantId TenantId { get; set; }

    /// <summary>Business time, UTC. Partition key and leading time of the history ordering rule.</summary>
    public DateTime EventTime { get; set; }

    public Guid EventId { get; set; }

    public Guid UnitId { get; set; }

    /// <summary>From <c>seq_unit_event</c>; monotonic, so per-tenant monotonic. Guards the projector.</summary>
    public long Seq { get; set; }

    /// <summary><see cref="Contracts.UnitEventType"/> and later phases' vocabularies.</summary>
    public required string EventType { get; set; }

    public string? BizStep { get; set; }

    public string? Disposition { get; set; }

    public Guid? LocationId { get; set; }

    public Guid LegalEntityId { get; set; }

    public required string ActorRef { get; set; }

    public string? DocumentType { get; set; }

    public Guid? DocumentId { get; set; }

    public int? DocumentLine { get; set; }

    /// <summary>Repository time, UTC (EPCIS recordTime); second key of the ordering rule.</summary>
    public DateTime RecordedTime { get; set; }

    /// <summary>Finance period <c>yyyy-MM</c>, which may differ from business time (case 33).</summary>
    public string? PostingPeriod { get; set; }

    /// <summary>Deterministic hash of (source, document, line, unit, event type, business time); SHA-256 hex truncated to 64.</summary>
    public required string IdempotencyKey { get; set; }

    public Guid? ReversesEventId { get; set; }

    /// <summary>JSON extension data (ILMD, correction references, before/after values).</summary>
    public string? Attributes { get; set; }
}

/// <summary>The live, partitioned ledger.</summary>
public sealed class UnitEvent : UnitEventRow;

/// <summary>Switched-out cold partitions; same columns, same scheme, still queryable for reprints and audits.</summary>
public sealed class UnitEventArchive : UnitEventRow;

/// <summary>
/// Scratch table on the same scheme for the second archival of a month: when the archive partition is no longer
/// empty, the live partition is switched here (metadata), copied into the archive per tenant (append), and the
/// stage partition truncated. Empty between lifecycle runs; never read by the application.
/// </summary>
public sealed class UnitEventArchiveStage : UnitEventRow;

/// <summary>
/// One row per unit with everything an operational read needs (ADR-0001 UnitCurrentState). Maintained in the
/// same transaction as every event write (coupling 2, P1-T04); <see cref="LastEventSeq"/> only ever grows.
/// </summary>
public sealed class UnitCurrent : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid UnitId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? ModelVersionId { get; set; }

    public Guid? LotId { get; set; }

    public required string LifecycleStatus { get; set; }

    public required string Disposition { get; set; }

    public string? ConditionGrade { get; set; }

    public Guid? LocationId { get; set; }

    public string? LocationKind { get; set; }

    public Guid? CustodyPartyRef { get; set; }

    public string? CustodyKind { get; set; }

    public Guid? OwnerPartyRef { get; set; }

    public Guid? OpenOwnershipEpisodeId { get; set; }

    public Guid? OpenCustodyEpisodeId { get; set; }

    /// <summary>JSON array of entitlement ids; SQL Server has no array type. Written by MOD-06 from P2.</summary>
    public string LiveEntitlementIds { get; set; } = "[]";

    public DateOnly? WarrantyEndMax { get; set; }

    /// <summary>JSON array of <see cref="Contracts.OverlayFlag"/> values.</summary>
    public string OverlayFlags { get; set; } = "[]";

    public long LastEventSeq { get; set; }

    public DateTime LastEventTime { get; set; }

    public string? Attributes { get; set; }

    /// <summary>SQL Server <c>rowversion</c>; optimistic concurrency for the projector.</summary>
    public byte[] RowVersion { get; set; } = [];
}
