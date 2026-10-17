using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>Durable idempotency receipt for the module side of the Manufacturing contract.</summary>
public sealed class ManufacturingMessageReceipt : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid MessageId { get; set; }
    public required string MessageType { get; set; }
    public required string PayloadHash { get; set; }
    public required string ResultJson { get; set; }
    public DateTime ProcessedAt { get; set; }
}
