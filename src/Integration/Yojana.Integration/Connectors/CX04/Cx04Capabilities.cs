using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04CapabilityDeclaration(IReadOnlyDictionary<string, string> Messages,
    IReadOnlyList<string> CaptureModes, ConnectorThrottleDeclaration Throttle);

public static class Cx04Capabilities
{
    public const string ConnectorCode = "CX04";

    private static readonly IReadOnlyDictionary<string, string> MessageSupport =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ItemUpserted"] = CapabilitySupport.Native,
            ["BomUpserted"] = CapabilitySupport.Unsupported,
            ["PartyUpserted"] = CapabilitySupport.Native,
            ["GoodsReceiptPosted"] = CapabilitySupport.Native,
            ["ProductionCompleted"] = CapabilitySupport.Native,
            ["DeliveryPosted"] = CapabilitySupport.Native,
            ["InvoicePosted"] = CapabilitySupport.Native,
            ["ReturnReceived"] = CapabilitySupport.Native,
            ["TransferPosted"] = CapabilitySupport.Native,
            ["InventoryAdjusted"] = CapabilitySupport.Native,
            ["WarrantyLineSold"] = CapabilitySupport.Native,
            ["DocumentCancelled"] = CapabilitySupport.Native,
            ["CreateReplacementDelivery"] = CapabilitySupport.Native,
            ["CreateCreditMemo"] = CapabilitySupport.Native,
            ["PostWarrantyExpense"] = CapabilitySupport.Native,
            ["ChangeStockStatusOrLocation"] = CapabilitySupport.Native,
            ["AttachDocument"] = CapabilitySupport.Native,
            ["CreateServiceOrder"] = CapabilitySupport.Unsupported,
        };

    public static int PublishedRequestsPerMinute(Cx04LicenceTier tier) => tier switch
    {
        Cx04LicenceTier.Essentials => 50,
        Cx04LicenceTier.Select => 100,
        Cx04LicenceTier.Prime => 150,
        Cx04LicenceTier.Enterprise => 300,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    public static Cx04CapabilityDeclaration For(Cx04ConnectionSettings settings)
    {
        var licensed = settings.RequestsPerMinute ?? PublishedRequestsPerMinute(settings.LicenceTier);
        var governed = Math.Max(1, licensed * 4 / 5);
        return new(MessageSupport, ["poll", "push_hint"], new(ConnectorCode,
            new(null, governed, null, Math.Min(10, governed), 1, TimeSpan.FromMinutes(5))));
    }
}
