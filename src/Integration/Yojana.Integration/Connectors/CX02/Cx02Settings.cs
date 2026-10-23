using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX02;

public sealed record Cx02ConnectionSettings(
    Uri ServiceUri,
    string DirectoryTenant,
    string TenantSegment,
    string Environment,
    Guid CompanyId,
    string ExtensionVersion,
    Uri? NotificationBaseUri = null,
    Uri? TokenUri = null,
    int PollPageSize = 200,
    int PageCap = 20_000,
    int RefreshTokenEarlySeconds = 300,
    int RenewSubscriptionsBeforeHours = 12,
    bool BusinessEventHintsEnabled = true)
{
    public Cx02ConnectionSettings Validate()
    {
        if (!ServiceUri.IsAbsoluteUri || ServiceUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The CX02 service URL must be an absolute HTTP or HTTPS URL.");
        if (TokenUri is not null && (!TokenUri.IsAbsoluteUri || TokenUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("The CX02 token URL must be an absolute HTTP or HTTPS URL.");
        if (NotificationBaseUri is not null && (!NotificationBaseUri.IsAbsoluteUri
            || NotificationBaseUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("The CX02 notification URL must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(DirectoryTenant) || string.IsNullOrWhiteSpace(TenantSegment)
            || string.IsNullOrWhiteSpace(Environment) || CompanyId == Guid.Empty)
            throw new InvalidOperationException("CX02 tenant, environment and company settings are required.");
        if (!Version.TryParse(ExtensionVersion, out var version) || version < new Version(1, 0))
            throw new InvalidOperationException("The CX02 extension version must be 1.0 or later.");
        if (PollPageSize is < 1 or > 20_000 || PageCap is < 1 or > 20_000 || PollPageSize > PageCap)
            throw new InvalidOperationException("CX02 page sizes must be between 1 and 20000, with the poll size within the cap.");
        if (RefreshTokenEarlySeconds is < 30 or > 1_800 || RenewSubscriptionsBeforeHours is < 1 or > 48)
            throw new InvalidOperationException("CX02 refresh and renewal horizons are invalid.");
        return this;
    }
}

public sealed record Cx02ClientCredential(string ClientId, string ClientSecret, string ClientState);

internal sealed record Cx02SubscriptionState(Guid SubscriptionId, string Resource, bool Custom,
    string Etag, DateTimeOffset ExpiresAt);

internal sealed record Cx02NotificationState(
    IReadOnlyList<Cx02SubscriptionState> Subscriptions,
    bool BusinessEventsSubscribed = false)
{
    public static Cx02NotificationState Empty { get; } = new([]);
}

public sealed class Cx02Settings(ITenantSettings settings)
{
    public static string ConnectionKey(Guid connectionId) => $"integration.cx02.connection.{connectionId:N}";
    internal static string NotificationKey(Guid connectionId) => $"integration.cx02.notifications.{connectionId:N}";

    public static JsonElement ToJson(Cx02ConnectionSettings value) =>
        JsonSerializer.SerializeToElement(value, IntegrationContracts.Json);

    public async Task<Cx02ConnectionSettings?> TryGetAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await settings.GetAsync(ConnectionKey(connectionId), cancellationToken) is { } value
            ? JsonSerializer.Deserialize<Cx02ConnectionSettings>(value, IntegrationContracts.Json)?.Validate() : null;

    public async Task<Cx02ConnectionSettings> GetAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await TryGetAsync(connectionId, cancellationToken)
        ?? throw new InvalidOperationException("CX02 connection settings are not configured.");

    public Task SetAsync(Guid connectionId, Cx02ConnectionSettings value, CancellationToken cancellationToken) =>
        settings.SetAsync(ConnectionKey(connectionId), JsonSerializer.Serialize(value.Validate(), IntegrationContracts.Json),
            cancellationToken);

    internal async Task<Cx02NotificationState> NotificationsAsync(Guid connectionId,
        CancellationToken cancellationToken) => await settings.GetAsync(NotificationKey(connectionId), cancellationToken)
        is { } value ? JsonSerializer.Deserialize<Cx02NotificationState>(value, IntegrationContracts.Json)
            ?? Cx02NotificationState.Empty : Cx02NotificationState.Empty;

    internal Task SetNotificationsAsync(Guid connectionId, Cx02NotificationState value,
        CancellationToken cancellationToken) => settings.SetAsync(NotificationKey(connectionId),
        JsonSerializer.Serialize(value, IntegrationContracts.Json), cancellationToken);
}
