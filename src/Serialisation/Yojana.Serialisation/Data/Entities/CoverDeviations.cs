using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>Report projection; historical entitlement rows remain the authority.</summary>
public sealed class UnitCoverDeviation : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UnitId { get; set; }
    public DateOnly SaleDate { get; set; }
    public string? SaleDocumentType { get; set; }
    public Guid? SaleDocumentId { get; set; }
    public Guid? CustomerPartyRef { get; set; }
    public Guid ItemId { get; set; }
    public string? ItemFamily { get; set; }
    public bool Changed { get; set; }
    public int ChangeCount { get; set; }
    public bool Added { get; set; }
    public bool Upgraded { get; set; }
    public bool CoverChanged { get; set; }
    public bool Amended { get; set; }
    public bool ClosedEarly { get; set; }
    public bool Suspended { get; set; }
    public DateTime LastChangeAt { get; set; }
}

public sealed class UnitCoverDeviationChange : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UnitId { get; set; }
    public Guid EventId { get; set; }
    public Guid EntitlementId { get; set; }
    public required string Kind { get; set; }
    public DateTime EventTime { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int MonthsAfterSale { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
}

public sealed class UnitCoverDeviationValue : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UnitId { get; set; }
    public required string Currency { get; set; }
    public decimal Amount { get; set; }
}

public sealed class CoverDeviationBackfillCheckpoint : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid? CursorUnitId { get; set; }
    public bool Complete { get; set; }
    public long UnitsProcessed { get; set; }
}
