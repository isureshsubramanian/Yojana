using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class UnitConditionEvent : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConditionEventId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public required string Grade { get; set; }
    public string? FCode { get; set; }
    public string? CCode { get; set; }
    public required string ValuationRule { get; set; }
    public required string InspectorRef { get; set; }
    public DateTime GradedAt { get; set; }
    public Guid EventId { get; set; }
}

public sealed class SanitisationRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid SanitisationRecordId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public required string Manufacturer { get; set; }
    public required string Model { get; set; }
    public required string MediaSerial { get; set; }
    public required string MediaType { get; set; }
    public required string Method { get; set; }
    public required string Tool { get; set; }
    public string? ToolVersion { get; set; }
    public required string VerificationMethod { get; set; }
    public required string OperatorRef { get; set; }
    public DateTime SanitisedAt { get; set; }
    public required string CertificateRef { get; set; }
    public string? SignatureRef { get; set; }
    public Guid EventId { get; set; }
}

public sealed class TestRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TestRecordId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public required string TestKind { get; set; }
    public required string Result { get; set; }
    public required string MeasurementsJson { get; set; }
    public required string Equipment { get; set; }
    public required string OperatorRef { get; set; }
    public DateTime TestedAt { get; set; }
    public Guid EventId { get; set; }
}

public sealed class UsageReading : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid UsageReadingId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public required string MeterKind { get; set; }
    public decimal Value { get; set; }
    public required string Unit { get; set; }
    public DateTime ReadAt { get; set; }
    public bool MeterReplacement { get; set; }
    public required string OperatorRef { get; set; }
    public Guid EventId { get; set; }
}

public sealed class UnitAttributeHistory : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid AttributeHistoryId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public required string ActorRef { get; set; }
    public Guid EventId { get; set; }
}

/// <summary>An independently auditable period in a unit's service life; refurbishment commands own when it opens and closes.</summary>
public sealed class LifeEpisode : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid LifeEpisodeId { get; set; }
    public Guid UnitId { get; set; }
    public required string Kind { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Guid? WorkOrderId { get; set; }
    public int? CycleNumber { get; set; }
    public required string ActorRef { get; set; }
    public Guid OpeningEventId { get; set; }
    public Guid? ClosingEventId { get; set; }
}
