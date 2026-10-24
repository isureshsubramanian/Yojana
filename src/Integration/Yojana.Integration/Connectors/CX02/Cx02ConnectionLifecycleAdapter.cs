using System.Text.Json;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX02;

public sealed class Cx02ConnectionLifecycleAdapter(
    Cx02Settings settings,
    Cx02Client client,
    Cx02WebhookMaintenance notifications) : IConnectionLifecycleAdapter
{
    public ConnectionFeatures Features { get; } = new(TenantSideExtension: true, InteractiveSignIn: false);

    public ConnectionRegistrationForm RegistrationForm => new(
        [
            new("service_uri", "Service address", "url"),
            new("directory_tenant", "Application directory tenant", "text"),
            new("tenant_segment", "API tenant segment", "text"),
            new("environment", "Environment", "text", "sandbox"),
            new("company_id", "Company identifier", "text"),
            new("extension_version", "Extension version", "text", "1.0"),
            new("notification_base_uri", "Public notification address", "url"),
        ],
        Cx02Client.CredentialPurpose,
        [new("client_id", "Application client identifier", "text"),
         new("client_secret", "Application client secret", "password"),
         new("client_state", "Notification client state", "password")]);

    public string DefaultOutboundMode => "none";
    public IReadOnlyList<string> CredentialPurposes { get; } = [Cx02Client.CredentialPurpose];

    public void ValidateSettings(JsonElement value) => _ = Read(value);

    public async Task<ConnectionDeclaration> ConfigureAsync(Guid connectionId, JsonElement value,
        CancellationToken ct)
    {
        var configured = Read(value);
        await settings.SetAsync(connectionId, configured, ct);
        var declaration = Cx02Capabilities.Declaration;
        return new(declaration.Messages, declaration.Throttle,
            Cx02Protocol.PollEntities.ToDictionary(entity => entity.EntityType,
                entity => entity.CanonicalMessage, StringComparer.Ordinal),
            configured.NotificationBaseUri is null ? []
                : Cx02Protocol.NotificationEntities.Select(entity => entity.EntityType).ToList());
    }

    public async Task<ConnectionProbe> SmokeReadAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        using var companies = await client.GetAsync(connectionId,
            new(Cx02Protocol.StandardRoot(configured), "companies"), ct);
        var companyReachable = companies.RootElement.TryGetProperty("value", out var values)
            && values.EnumerateArray().Any(value => value.TryGetProperty("id", out var id)
                && id.TryGetGuid(out var found) && found == configured.CompanyId);
        var items = await client.ReadCollectionAsync(connectionId,
            Cx02Protocol.Collection(configured, Cx02Protocol.Entity("items")), 1, configured.PageCap,
            ct);
        var tracked = await client.ReadCollectionAsync(connectionId,
            Cx02Protocol.Collection(configured, Cx02Protocol.Entity("tracked_ledger_entries")), 1,
            configured.PageCap, ct);
        return new(JsonSerializer.SerializeToElement(new
        {
            company_reachable = companyReachable,
            item_read_succeeded = items.Count >= 0,
            extension_read_succeeded = tracked.Count >= 0,
            extension_version = configured.ExtensionVersion,
            application_registration_per_tenant = true,
            full_access_role_allowed = false,
        }, IntegrationContracts.Json), NotificationsSupported: configured.NotificationBaseUri is not null);
    }

    public async Task<JsonElement> ReadSettingsAsync(Guid connectionId, CancellationToken ct) =>
        Cx02Settings.ToJson(await settings.GetAsync(connectionId, ct));

    public async Task<Uri?> ServiceUriAsync(Guid connectionId, CancellationToken ct) =>
        (await settings.GetAsync(connectionId, ct)).ServiceUri;

    public async Task<ConnectionProbe?> LatestProbeAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.TryGetAsync(connectionId, ct);
        return configured is null ? null : new(JsonSerializer.SerializeToElement(new
        {
            extension_version = configured.ExtensionVersion,
            notification_endpoint_configured = configured.NotificationBaseUri is not null,
            company_id = configured.CompanyId,
        }, IntegrationContracts.Json), configured.NotificationBaseUri is not null);
    }

    public Task UnsubscribeAsync(Guid connectionId, CancellationToken ct) =>
        notifications.UnsubscribeAsync(connectionId, ct);

    public Task SuspendNotificationsAsync(Guid connectionId, CancellationToken ct) =>
        notifications.PauseAsync(connectionId, ct);

    public Task ResumeNotificationsAsync(Guid connectionId, CancellationToken ct) =>
        notifications.ResumeAsync(connectionId, ct);

    private static Cx02ConnectionSettings Read(JsonElement value) =>
        (value.Deserialize<Cx02ConnectionSettings>(IntegrationContracts.Json)
            ?? throw new InvalidOperationException("CX02 settings are required.")).Validate();
}
