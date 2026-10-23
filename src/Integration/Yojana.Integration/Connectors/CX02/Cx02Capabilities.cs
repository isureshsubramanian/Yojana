using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.Connectors.CX02;

public sealed record Cx02CapabilityDeclaration(
    IReadOnlyDictionary<string, string> Messages,
    IReadOnlyList<string> CaptureModes,
    ConnectorThrottleDeclaration Throttle);

public static class Cx02Capabilities
{
    public const string ConnectorCode = "CX02";

    private static readonly IReadOnlyDictionary<string, string> MessageSupport =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ItemUpserted"] = CapabilitySupport.Native,
            ["PartyUpserted"] = CapabilitySupport.Native,
            ["InvoicePosted"] = CapabilitySupport.Native,
            ["WarrantyLineSold"] = CapabilitySupport.Native,
            ["DocumentCancelled"] = CapabilitySupport.Native,
            ["GoodsReceiptPosted"] = CapabilitySupport.TenantCode,
            ["ProductionCompleted"] = CapabilitySupport.TenantCode,
            ["DeliveryPosted"] = CapabilitySupport.TenantCode,
            ["ReturnReceived"] = CapabilitySupport.TenantCode,
            ["TransferPosted"] = CapabilitySupport.TenantCode,
            ["InventoryAdjusted"] = CapabilitySupport.TenantCode,
            ["CreateCreditMemo"] = CapabilitySupport.Native,
            ["PostWarrantyExpense"] = CapabilitySupport.Native,
            ["CreateReplacementDelivery"] = CapabilitySupport.TenantCode,
            ["ChangeStockStatusOrLocation"] = CapabilitySupport.TenantCode,
            ["CreateServiceOrder"] = CapabilitySupport.TenantCode,
            ["AttachDocument"] = CapabilitySupport.Native,
            ["PushNativeWarrantyObject"] = CapabilitySupport.Unsupported,
        };

    public static Cx02CapabilityDeclaration Declaration { get; } = new(
        MessageSupport, ["poll", "webhook", "business_event"],
        new(ConnectorCode, new(RequestsPerSecond: 20, RequestsPerMinute: 1_200,
            RequestsPerDay: null, Burst: 20, MaxConcurrentCalls: 5, RetryAfterCeiling: TimeSpan.FromMinutes(5))));
}
