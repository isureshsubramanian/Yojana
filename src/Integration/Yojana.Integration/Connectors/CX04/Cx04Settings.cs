using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX04;

public enum Cx04LicenceTier
{
    Essentials,
    Select,
    Prime,
    Enterprise,
}

public enum Cx04AllocationQuantityMode
{
    LineAndAllocations,
    AllocationsOnly,
}

public sealed record Cx04ConnectionSettings(
    Uri ServiceUri,
    string ClientId,
    Uri RedirectUri,
    Cx04LicenceTier LicenceTier,
    string RefreshExpiryMode = Cx04RefreshExpiryModes.Sliding,
    int RefreshLifetimeDays = 90,
    int? RequestsPerMinute = null,
    Cx04AllocationQuantityMode AllocationQuantityMode = Cx04AllocationQuantityMode.LineAndAllocations,
    string EndpointVersion = Cx04Protocol.EndpointVersion,
    string ManufacturingEndpointVersion = Cx04Protocol.ManufacturingEndpointVersion,
    string? NotificationSecretSha256 = null,
    int PollPageSize = 200,
    int PageCap = 20_000,
    int RefreshEarlySeconds = 300,
    int AttachmentSizeLimitBytes = 25 * 1024 * 1024)
{
    public Cx04ConnectionSettings Validate()
    {
        if (!ServiceUri.IsAbsoluteUri || ServiceUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The CX04 service URL must be an absolute HTTP or HTTPS URL.");
        if (!RedirectUri.IsAbsoluteUri || RedirectUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The CX04 redirect URL must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 500)
            throw new InvalidOperationException("The CX04 connected-application identifier is required.");
        if (EndpointVersion != Cx04Protocol.EndpointVersion
            || ManufacturingEndpointVersion != Cx04Protocol.ManufacturingEndpointVersion)
            throw new InvalidOperationException("The CX04 endpoint versions must match the adapter contract pins.");
        if (RefreshExpiryMode is not (Cx04RefreshExpiryModes.Absolute or Cx04RefreshExpiryModes.Sliding)
            || RefreshLifetimeDays is < 1 or > 365)
            throw new InvalidOperationException("CX04 refresh expiry must be absolute or sliding from 1 to 365 days.");
        var published = Cx04Capabilities.PublishedRequestsPerMinute(LicenceTier);
        if (RequestsPerMinute is < 1 || RequestsPerMinute > published)
            throw new InvalidOperationException("The CX04 request limit must not exceed the selected licence tier.");
        if (PollPageSize is < 1 or > 20_000 || PageCap is < 1 or > 20_000 || PollPageSize > PageCap)
            throw new InvalidOperationException("CX04 page sizes must be between 1 and 20000, with the poll size within the cap.");
        if (RefreshEarlySeconds is < 30 or > 1_800 || AttachmentSizeLimitBytes is < 1 or > 100 * 1024 * 1024)
            throw new InvalidOperationException("CX04 refresh or attachment limits are invalid.");
        if (NotificationSecretSha256 is not null
            && (NotificationSecretSha256.Length != 64 || !NotificationSecretSha256.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("The CX04 notification secret hash must be a SHA-256 hex value.");
        return this;
    }
}

public sealed class Cx04Settings(ITenantSettings settings)
{
    public static string Key(Guid connectionId) => $"integration.cx04.connection.{connectionId:N}";

    public async Task<Cx04ConnectionSettings?> TryGetAsync(Guid connectionId, CancellationToken ct) =>
        await settings.GetAsync(Key(connectionId), ct) is { } value
            ? JsonSerializer.Deserialize<Cx04ConnectionSettings>(value, IntegrationContracts.Json)?.Validate()
            : null;

    public async Task<Cx04ConnectionSettings> GetAsync(Guid connectionId, CancellationToken ct) =>
        await TryGetAsync(connectionId, ct)
        ?? throw new InvalidOperationException("CX04 connection settings are not configured.");

    public Task SetAsync(Guid connectionId, Cx04ConnectionSettings value, CancellationToken ct) =>
        settings.SetAsync(Key(connectionId), JsonSerializer.Serialize(value.Validate(), IntegrationContracts.Json), ct);

    public static JsonElement ToJson(Cx04ConnectionSettings value) =>
        JsonSerializer.SerializeToElement(value, IntegrationContracts.Json);
}
