using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX04;

public sealed class Cx04ConnectionLifecycleAdapter(IntegrationDbContext db, Cx04Settings settings,
    Cx04Client client, Cx04Authorisation authorisation, IClock clock) : IConnectionLifecycleAdapter
{
    public ConnectionFeatures Features { get; } = new(TenantSideExtension: false, InteractiveSignIn: true);

    public ConnectionRegistrationForm RegistrationForm => new(
        [
            new("service_uri", "Service address", "url"),
            new("client_id", "Connected application identifier", "text"),
            new("redirect_uri", "Authorisation callback address", "url"),
            new("licence_tier", "Licence tier", "select", "essentials"),
            new("refresh_expiry_mode", "Refresh expiry mode", "select", Cx04RefreshExpiryModes.Sliding),
            new("refresh_lifetime_days", "Refresh lifetime in days", "number", "90"),
            new("allocation_quantity_mode", "Allocation quantity reading", "select", "line_and_allocations"),
            new("notification_secret_sha256", "Push secret SHA-256", "text"),
        ], "", []);

    public string DefaultOutboundMode => "none";
    public string InitialConnectionStatus => Data.Entities.ConnectionStatus.Paused;
    public int PollOverlapSeconds => 2 * 24 * 60 * 60;
    public IReadOnlyList<string> CredentialPurposes { get; } = [];

    public void ValidateSettings(JsonElement value) => _ = Read(value);

    public async Task<ConnectionDeclaration> ConfigureAsync(Guid connectionId, JsonElement value,
        CancellationToken ct)
    {
        var configured = Read(value);
        await settings.SetAsync(connectionId, configured, ct);
        var state = await db.Cx04AuthorisationStates.SingleOrDefaultAsync(row => row.ConnectionId == connectionId, ct);
        if (state is null)
        {
            state = new()
            {
                TenantId = db.CurrentTenantId, ConnectionId = connectionId,
                Status = Cx04AuthorisationStatus.ConsentRequired,
                RefreshExpiryMode = configured.RefreshExpiryMode,
                RefreshLifetimeDays = configured.RefreshLifetimeDays,
                UpdatedAt = clock.UtcNow,
            };
            db.Cx04AuthorisationStates.Add(state);
            await db.SaveChangesAsync(ct);
        }
        var declaration = Cx04Capabilities.For(configured);
        return new(declaration.Messages, declaration.Throttle,
            Cx04Protocol.Entities.ToDictionary(value => value.Key, value => value.Value.CanonicalMessage,
                StringComparer.Ordinal),
            string.IsNullOrWhiteSpace(configured.NotificationSecretSha256)
                ? [] : Cx04Protocol.Entities.Keys.ToList());
    }

    public async Task<ConnectionProbe> SmokeReadAsync(Guid connectionId, CancellationToken ct)
    {
        var auth = await authorisation.ReadAsync(connectionId, ct);
        if (auth.Status != Cx04AuthorisationStatus.Active)
            return new(JsonSerializer.SerializeToElement(new
            {
                consent_required = true,
                endpoint_version = Cx04Protocol.EndpointVersion,
                production_completions_read_only = true,
            }, IntegrationContracts.Json), false);
        var configured = await settings.GetAsync(connectionId, ct);
        using var discovery = await client.GetAsync(connectionId, new(configured.ServiceUri, "entity"), ct);
        var endpoint = discovery.RootElement.GetProperty("endpoints").EnumerateArray().Any(value =>
            value.GetProperty("name").GetString() == "Default"
            && value.GetProperty("version").GetString() == Cx04Protocol.EndpointVersion);
        return new(JsonSerializer.SerializeToElement(new
        {
            consent_required = false, endpoint_version = Cx04Protocol.EndpointVersion,
            endpoint_available = endpoint, production_completions_read_only = true,
            licence_tier = configured.LicenceTier.ToString().ToLowerInvariant(),
            refresh_expiry_mode = configured.RefreshExpiryMode,
            refresh_lifetime_days = configured.RefreshLifetimeDays,
        }, IntegrationContracts.Json), !string.IsNullOrWhiteSpace(configured.NotificationSecretSha256));
    }

    public async Task<JsonElement> ReadSettingsAsync(Guid connectionId, CancellationToken ct) =>
        Cx04Settings.ToJson(await settings.GetAsync(connectionId, ct));

    public async Task<Uri?> ServiceUriAsync(Guid connectionId, CancellationToken ct) =>
        (await settings.GetAsync(connectionId, ct)).ServiceUri;

    public async Task<ConnectionProbe?> LatestProbeAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.TryGetAsync(connectionId, ct);
        if (configured is null) return null;
        var auth = await authorisation.ReadAsync(connectionId, ct);
        return new(JsonSerializer.SerializeToElement(new
        {
            endpoint_version = configured.EndpointVersion,
            authorisation_status = auth.Status,
            refresh_chain_expires_at = auth.RefreshChainExpiresAt,
            production_completions_read_only = true,
        }, IntegrationContracts.Json), !string.IsNullOrWhiteSpace(configured.NotificationSecretSha256));
    }

    public Task<ConnectionAuthorisationView?> AuthorisationAsync(Guid connectionId, CancellationToken ct) =>
        ReadAuthorisationAsync(connectionId, ct);

    public async Task UnsubscribeAsync(Guid connectionId, CancellationToken ct)
    {
        await db.ConnectionNotificationSources.Where(row => row.ConnectionId == connectionId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, false), ct);
        await client.SignOutCurrentAsync(connectionId, ct);
    }

    public Task SuspendNotificationsAsync(Guid connectionId, CancellationToken ct) =>
        db.ConnectionNotificationSources.Where(row => row.ConnectionId == connectionId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, false), ct);

    public Task ResumeNotificationsAsync(Guid connectionId, CancellationToken ct) =>
        db.ConnectionNotificationSources.Where(row => row.ConnectionId == connectionId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, true), ct);

    private async Task<ConnectionAuthorisationView?> ReadAuthorisationAsync(Guid connectionId,
        CancellationToken ct) => await authorisation.ReadAsync(connectionId, ct);

    private static Cx04ConnectionSettings Read(JsonElement value) =>
        (value.Deserialize<Cx04ConnectionSettings>(IntegrationContracts.Json)
            ?? throw new InvalidOperationException("CX04 settings are required.")).Validate();
}
