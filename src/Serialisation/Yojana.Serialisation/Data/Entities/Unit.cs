using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// PhysicalUnit root (ADR-0001 "Core model: PhysicalUnit"). A surrogate that is never reused and never
/// deleted; every identifier, assignment, link, episode and event hangs off <see cref="UnitId"/>.
/// System-versioned: the history table answers "what did this row say on that date"; the acting user is on
/// the event ledger, never here.
/// </summary>
public sealed class Unit : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid UnitId { get; set; }

    /// <summary><see cref="Contracts.LifecycleStatus"/>.</summary>
    public required string LifecycleStatus { get; set; }

    /// <summary>JSON array of <see cref="Contracts.OverlayFlag"/> values; <c>[]</c> when none.</summary>
    public string OverlayFlags { get; set; } = "[]";

    /// <summary><see cref="Contracts.UnitOrigin"/>.</summary>
    public required string Origin { get; set; }

    /// <summary>False for trade-ins of foreign brands (case 22).</summary>
    public bool BrandIsOurs { get; set; } = true;

    public Guid? LotId { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    public Guid? ConfigurationSnapshotId { get; set; }

    /// <summary>Lineage for scrap-and-reuse (case 13) and remanufacture: the unit this one was derived from.</summary>
    public Guid? DerivedFromUnitId { get; set; }

    /// <summary>Set when <see cref="LifecycleStatus"/> is <c>merged</c>: the surviving unit.</summary>
    public Guid? MergedIntoUnitId { get; set; }

    /// <summary><see cref="Contracts.SerialisationPoint"/>, copied from the item at creation.</summary>
    public required string SerialisationPoint { get; set; }

    /// <summary>Retention class from the item; drives archival, never deletion.</summary>
    public required string RetentionClass { get; set; }

    /// <summary><see cref="Contracts.HistoryDepth"/>.</summary>
    public string HistoryDepth { get; set; } = Contracts.HistoryDepth.Full;

    public DateTime CreatedAt { get; set; }

    /// <summary>The event that created the row (<c>unit.commissioned</c> or <c>unit.created_pending_identifier</c>).</summary>
    public Guid CreatedEventId { get; set; }
}

/// <summary>
/// One identifier string on a unit (Identity rules 1–6). Correction never edits: the old row closes with
/// <see cref="ValidTo"/> and <see cref="SupersededByIdentifierId"/>, a new row opens.
/// </summary>
public sealed class UnitIdentifier : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid IdentifierId { get; set; }

    public Guid UnitId { get; set; }

    /// <summary><see cref="Contracts.IdentifierType"/>.</summary>
    public required string Type { get; set; }

    /// <summary><see cref="Contracts.IdentifierScheme"/>.</summary>
    public required string Scheme { get; set; }

    public Guid? IssuerPartyRef { get; set; }

    /// <summary>GS1 company prefix, DoD CAGE, ISO 15459 issuing agency plus company id, or null.</summary>
    public string? EnterpriseIdentifier { get; set; }

    /// <summary>Part, GTIN or lot the identifier is qualified by (Construct 2 UIIs, SGTIN).</summary>
    public string? Qualifier { get; set; }

    /// <summary>Exactly as captured, scanner artefacts included.</summary>
    public required string RawValue { get; set; }

    /// <summary>After the type's normaliser; the value uniqueness and lookup use.</summary>
    public required string NormalisedValue { get; set; }

    /// <summary>The item by default, the owner party for asset tags, <c>TENANT</c> under global uniqueness.</summary>
    public required string ScopeKey { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public Guid? SupersededByIdentifierId { get; set; }

    /// <summary>The identifier this row replaced (asset-tag re-pointing on replacement, re-labelling).</summary>
    public Guid? SupersedesIdentifierId { get; set; }

    public string? CorrectionReason { get; set; }

    /// <summary>Vendor serials known to collide across genuinely different units (case 131), set by the approver.</summary>
    public bool DuplicateAcknowledged { get; set; }

    public required string ActorRef { get; set; }

    public DateTime RecordedTime { get; set; }

    /// <summary>The event that wrote the row.</summary>
    public Guid EventId { get; set; }
}

/// <summary>Effective-dated item and model version of a unit (case 42).</summary>
public sealed class UnitItemAssignment : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid AssignmentId { get; set; }

    public Guid UnitId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>Distinct from the SKU: a device identifier per version (FDA 830.50).</summary>
    public Guid? ModelVersionId { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    /// <summary><see cref="Contracts.ItemAssignmentReason"/>.</summary>
    public required string Reason { get; set; }

    public Guid EventId { get; set; }
}

/// <summary>
/// Parent/child link of the as-built and as-maintained trees (Identity rules 8, 9). A serialised child
/// carries <see cref="ChildUnitId"/>; a lot or non-serialised child carries item, lot and quantity.
/// </summary>
public sealed class AssemblyLink : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid LinkId { get; set; }

    public Guid ParentUnitId { get; set; }

    public Guid? ChildUnitId { get; set; }

    public Guid? ChildItemId { get; set; }

    public Guid? ChildLotId { get; set; }

    public decimal? Quantity { get; set; }

    /// <summary><see cref="Contracts.AssemblyRelation"/>.</summary>
    public required string Relation { get; set; }

    public string? Slot { get; set; }

    public DateTime InstalledAt { get; set; }

    /// <summary>Repository time for a link captured after the physical fit; null on rows created before P3-T05.</summary>
    public DateTime? RecordedAt { get; set; }

    public DateTime? RemovedAt { get; set; }

    public Guid InstallingEventId { get; set; }

    public Guid? RemovingEventId { get; set; }

    /// <summary>The phantom item the child was consumed through, when the BOM had one.</summary>
    public Guid? ThroughPhantomItemId { get; set; }

    /// <summary><see cref="Contracts.ReleaseStatus"/>.</summary>
    public string? ReleaseStatus { get; set; }

    public string? ReleaseDocRef { get; set; }
}

/// <summary>
/// Immutable as-built snapshot frozen at release (certificate version 1 reads it). A new version is written
/// only by a later release (refurbishment, reassembly); earlier versions are never changed.
/// </summary>
public sealed class BuildRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid BuildRecordId { get; set; }

    public Guid UnitId { get; set; }

    public int Version { get; set; }

    public DateTime ReleasedAt { get; set; }

    public Guid ReleaseEventId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? ModelVersionId { get; set; }

    public string? Revision { get; set; }

    public Guid? FacilityId { get; set; }

    /// <summary>JSON: identifiers, source lots, as-built tree, test and inspection results, build date.</summary>
    public required string Payload { get; set; }

    /// <summary>SHA-256 of <see cref="Payload"/>, lower-case hex.</summary>
    public required string PayloadHash { get; set; }

    public string? ReleaseSignatureRef { get; set; }
}

/// <summary>
/// A block or randomised list of serials reserved for an order (case 7, Identity rule 13). A reservation is
/// not an identity: no unit exists until commissioning.
/// </summary>
public sealed class SerialRange : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid RangeId { get; set; }

    public required string Scheme { get; set; }

    public string? EnterpriseIdentifier { get; set; }

    public string? Qualifier { get; set; }

    public Guid? ItemId { get; set; }

    public string? StartValue { get; set; }

    public string? EndValue { get; set; }

    /// <summary>Numeric position of the start within the scheme's block; overlap detection compares ordinals.</summary>
    public long? StartOrdinal { get; set; }

    public long? EndOrdinal { get; set; }

    /// <summary>Equals the order quantity at allocation (count == quantity).</summary>
    public int Count { get; set; }

    public string? RandomisedListRef { get; set; }

    public Guid? AllocatedToPartyRef { get; set; }

    public string? AllocatedToDocumentType { get; set; }

    public Guid? AllocatedToDocumentId { get; set; }

    public DateTime ReservedAt { get; set; }

    public required string ReservedBy { get; set; }

    /// <summary><see cref="Contracts.SerialRangeStatus"/>.</summary>
    public required string Status { get; set; }

    public int CommissionedCount { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// One serial of a randomised allocation (a contiguous block needs no members: its serials are its ordinals). Also
/// records which unit took the serial, so range status and gaps are answerable per serial.
/// </summary>
public sealed class SerialRangeMember : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid RangeId { get; set; }

    public long Ordinal { get; set; }

    public required string Value { get; set; }

    public Guid? CommissionedUnitId { get; set; }
}
