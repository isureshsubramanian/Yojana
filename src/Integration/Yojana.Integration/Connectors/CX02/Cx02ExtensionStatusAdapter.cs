using System.Text.Json;
using Yojana.Integration.Connectors;

namespace Yojana.Integration.Connectors.CX02;

public sealed class Cx02ExtensionStatusAdapter(Cx02Settings settings, Cx02Client client)
    : IConnectionExtensionAdapter
{
    private static readonly IReadOnlyList<ConnectionExtensionPageDefinition> Pages =
    [
        new("trackedItemLedgerEntries", "Item ledger entries with serial and lot", "tracked_ledger_entries",
            "trackedItemLedgerEntries"),
        new("itemEntryRelations", "Item entry relations", null),
        new("reservationEntries", "Item tracking reservations", null),
        new("trackingSpecifications", "Tracking specifications", null),
        new("serviceItems", "Service items", null),
        new("postedTransferShipments", "Posted transfer shipments", "transfer_shipments",
            "postedTransferShipments"),
        new("postedTransferShipmentLines", "Posted transfer shipment lines", "transfer_shipments"),
        new("postedTransferReceipts", "Posted transfer receipts", "transfer_receipts",
            "postedTransferReceipts"),
        new("postedTransferReceiptLines", "Posted transfer receipt lines", "transfer_receipts"),
        new("postedReturnReceipts", "Posted return receipts", "return_receipts",
            "postedReturnReceipts"),
        new("postedReturnReceiptLines", "Posted return receipt lines", "return_receipts"),
        new("trackedDocumentRequests", "Post a tracked document", null, OutboundOnly: true),
    ];

    public ConnectionExtensionDeclaration Declaration { get; } = new(
        Cx02ExtensionAssets.ShippedVersion,
        Cx02Protocol.ExtensionVersion,
        PermissionSetWithoutFullAccessRole: true,
        Pages);

    public async Task<ConnectionExtensionSnapshot> SnapshotAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var state = await settings.NotificationsAsync(connectionId, cancellationToken);
        var notifications = state.Subscriptions.Select(subscription => new ConnectionExtensionNotification(
            PageCode(subscription.Resource), subscription.ExpiresAt.UtcDateTime)).ToList();
        return new(configured.ExtensionVersion, notifications, configured.RenewSubscriptionsBeforeHours);
    }

    public async Task<IReadOnlyList<ConnectionExtensionPageProbe>> ProbeAsync(Guid connectionId,
        bool outboundEnabled, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var results = new List<ConnectionExtensionPageProbe>(Pages.Count);
        foreach (var page in Pages)
        {
            if (page.OutboundOnly && !outboundEnabled)
            {
                results.Add(new(page.Code, ConnectionExtensionPageStatus.NotUsedYet,
                    "Outbound is off until cutover."));
                continue;
            }
            try
            {
                var uri = new Uri(Cx02Protocol.CustomRoot(configured),
                    $"companies({configured.CompanyId:D})/{page.Code}?$top=1");
                _ = await client.ReadCollectionAsync(connectionId, uri, 1, configured.PageCap,
                    cancellationToken);
                results.Add(new(page.Code, ConnectionExtensionPageStatus.Reachable, null));
            }
            catch (Cx02RemoteException exception)
            {
                var reason = string.IsNullOrWhiteSpace(exception.RemoteCode)
                    ? $"The page returned HTTP {(int)exception.StatusCode}."
                    : $"The page returned {exception.RemoteCode}.";
                results.Add(new(page.Code, ConnectionExtensionPageStatus.NotReachable, reason));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new(page.Code, ConnectionExtensionPageStatus.NotReachable,
                    "The page check could not complete."));
            }
        }
        return results;
    }

    public string InstallationGuide() => Cx02ExtensionAssets.InstallationGuide;

    private static string PageCode(string resource)
    {
        var segment = resource.Split('?', 2)[0].TrimEnd('/').Split('/').Last();
        var bracket = segment.IndexOf('(');
        return bracket < 0 ? segment : segment[..bracket];
    }
}

public static class Cx02ExtensionAssets
{
    private const string ManifestResource = "Yojana.Integration.CX02.Extension.app.json";
    private const string GuideResource = "Yojana.Integration.CX02.Extension.README.md";
    private static readonly Lazy<string> Version = new(ReadVersion);
    private static readonly Lazy<string> Guide = new(() => Read(GuideResource));

    public static string ShippedVersion => Version.Value;
    public static string InstallationGuide => Guide.Value;

    private static string ReadVersion()
    {
        using var document = JsonDocument.Parse(Read(ManifestResource));
        return document.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("The embedded extension manifest has no version.");
    }

    private static string Read(string name)
    {
        using var stream = typeof(Cx02ExtensionAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded extension asset '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
