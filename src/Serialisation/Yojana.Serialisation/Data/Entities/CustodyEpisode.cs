using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// One effective-dated ownership or custody row (ADR-0001 "Core model: CustodyEpisode"). At most one open row
/// per (unit, kind); a closed row is immutable. System-versioned for forensic history of the few fields that
/// may change while open (due date, confidence).
/// </summary>
public sealed class CustodyEpisode : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid EpisodeId { get; set; }

    public Guid UnitId { get; set; }

    public Guid? MergedFromUnitId { get; set; }

    /// <summary><see cref="Contracts.EpisodeKind"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>Pseudonymisable party pointer; null custody rows are "us" at a location.</summary>
    public Guid? PartyRef { get; set; }

    /// <summary><see cref="Contracts.PartyType"/>.</summary>
    public string? PartyType { get; set; }

    /// <summary>The group entity that is party to this episode (cases 15, 109–111).</summary>
    public Guid? LegalEntityId { get; set; }

    /// <summary>Custody only: warehouse, bin or site reference.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>Custody only: <see cref="Contracts.LocationKind"/>.</summary>
    public string? LocationKind { get; set; }

    /// <summary><see cref="Contracts.EpisodeReason"/>.</summary>
    public string? Reason { get; set; }

    public DateOnly? DueBack { get; set; }

    /// <summary><see cref="Contracts.EpisodeSource"/>.</summary>
    public string? Source { get; set; }

    /// <summary><see cref="Contracts.Confidence"/>.</summary>
    public string? Confidence { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public string? OpeningDocumentType { get; set; }

    public Guid? OpeningDocumentId { get; set; }

    public int? OpeningDocumentLine { get; set; }

    public Guid OpeningEventId { get; set; }

    public string? ClosingDocumentType { get; set; }

    public Guid? ClosingDocumentId { get; set; }

    public int? ClosingDocumentLine { get; set; }

    public Guid? ClosingEventId { get; set; }

    /// <summary>Inter-company (case 27).</summary>
    public bool PartyIsGroupMember { get; set; }

    /// <summary>Marketplace order id (case 51).</summary>
    public string? ExternalOrderRef { get; set; }

    /// <summary>Ownership only: the customer's purchase-order reference, snapshotted from the opening document.</summary>
    public string? CustomerPoRef { get; set; }

    /// <summary>Ownership only: the customer's own model or configuration reference for the unit.</summary>
    public string? CustomerItemRef { get; set; }
}
