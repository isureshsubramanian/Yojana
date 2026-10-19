using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01CapabilityDeclaration(
    IReadOnlyDictionary<string, string> Messages,
    bool WebhooksAvailable,
    IReadOnlyList<string> CaptureModes,
    ConnectorThrottleDeclaration Throttle);

public static class Cx01Capabilities
{
    public const string ConnectorCode = "CX01";

    private static readonly IReadOnlyDictionary<string, string> MessageSupport =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ItemUpserted"] = CapabilitySupport.Native,
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
            ["CreateReplacementDelivery"] = CapabilitySupport.Manual,
            ["CreateCreditMemo"] = CapabilitySupport.Manual,
            ["PostWarrantyExpense"] = CapabilitySupport.Manual,
            ["ChangeStockStatusOrLocation"] = CapabilitySupport.Unsupported,
            ["AttachDocument"] = CapabilitySupport.Native,
            ["CreateServiceOrder"] = CapabilitySupport.Native,
        };

    public static Cx01CapabilityDeclaration For(string release, bool probedWebhooksAvailable)
    {
        var webhooks = probedWebhooksAvailable && ReleaseFeaturePack(release) >= 2602;
        return new(MessageSupport, webhooks, webhooks ? ["poll", "webhook"] : ["poll"],
            new(ConnectorCode, new(null, 120, null, 8, 1, TimeSpan.FromMinutes(5))));
    }

    internal static int ReleaseFeaturePack(string release)
    {
        var digits = new string(release.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 && int.TryParse(digits[^4..], out var value) ? value : 0;
    }
}

public sealed class Cx01Onboarding(
    IntegrationDbContext db,
    ITenantContext tenant,
    Cx01Settings settings,
    ConnectionThrottleProfiles throttles,
    Cx01Client client)
{
    public async Task<Cx01CapabilityDeclaration> ProbeAndApplyAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var initial = Cx01Capabilities.For(configured.Release, configured.WebhooksAvailable);
        await throttles.OnboardAsync(new(tenant.TenantId, connectionId), initial.Throttle, cancellationToken);
        var probed = await client.ProbeReleaseAsync(connectionId, cancellationToken);
        var declaration = Cx01Capabilities.For(probed.Release, probed.WebhooksAvailable);
        var rows = await db.ConnectionCapabilities.Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).ToListAsync(cancellationToken);
        db.ConnectionCapabilities.RemoveRange(rows);
        db.ConnectionCapabilities.AddRange(declaration.Messages.Select(pair => new ConnectionCapability
        {
            TenantId = tenant.TenantId,
            ConnectionId = connectionId,
            CanonicalMessage = pair.Key,
            SupportLevel = pair.Value,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await throttles.UpdateAsync(new(tenant.TenantId, connectionId), declaration.Throttle.DefaultProfile,
            cancellationToken);
        await settings.SetAsync(connectionId, configured with
        {
            Release = probed.Release,
            WebhooksAvailable = declaration.WebhooksAvailable,
        }, cancellationToken);
        return declaration;
    }
}

public sealed record Cx01ReleaseProbe(string Release, bool WebhooksAvailable);
