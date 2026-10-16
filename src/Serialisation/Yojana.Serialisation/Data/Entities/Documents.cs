using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// A shipment line posted without serials (a 3PL ships and confirms later, case 24). The serials and the physical
/// ship time arrive with <c>ConfirmShipmentSerials</c>, which writes the <c>unit.shipped</c> events with that business
/// time and resolves this row; the document is never re-posted.
/// </summary>
public sealed class PendingShipmentLine : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid PendingId { get; set; }

    public required string DocumentType { get; set; }

    public Guid DocumentId { get; set; }

    public int DocumentLine { get; set; }

    public Guid ItemId { get; set; }

    public decimal Quantity { get; set; }

    public Guid CustomerPartyRef { get; set; }

    public Guid? ShipToLocationId { get; set; }

    public Guid? CarrierPartyRef { get; set; }

    public DateTime PostedAt { get; set; }

    public Guid LegalEntityId { get; set; }

    public string? Source { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public Guid? ResolvedCommandId { get; set; }

    public int ResolvedCount { get; set; }
}

/// <summary>A Finance period (<c>yyyy-MM</c>) that is closed; later facts dated inside it post to the first open period (case 33).</summary>
public sealed class FinancePeriod : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public required string Period { get; set; }

    public DateTime ClosedAt { get; set; }

    public required string Source { get; set; }
}
