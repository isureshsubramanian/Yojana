using Yojana.Integration.Contracts;

namespace Yojana.Integration.Gateway;

/// <summary>The outbound contract types a connector may declare in its capability catalogue.</summary>
internal static class OutboundCapabilityTypes
{
    // Keep this as an array: EF Core translates array Contains to SQL IN for the cutover query.
    public static readonly string[] All =
    [
        nameof(CreateCreditMemo), nameof(PostWarrantyExpense), nameof(CreateReplacementDelivery),
        nameof(ChangeStockStatusOrLocation), nameof(CreateServiceOrder), nameof(AttachDocument),
        nameof(PushNativeWarrantyObject),
    ];
}
