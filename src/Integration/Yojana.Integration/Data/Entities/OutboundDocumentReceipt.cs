using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>One acknowledged remote document, including partial commands awaiting another document.</summary>
public sealed class OutboundDocumentReceipt : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ExternalId { get; set; } = "";
    public Guid OutboundCommandId { get; set; }
    public string? ChangeMarker { get; set; }
}
