using System.Text.Json;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

public sealed class Cx01ConnectionLifecycleAdapter(Cx01Settings settings, Cx01Client client,
    Cx01SurveyService survey, Cx01WebhookMaintenance notifications,
    Yojana.Integration.Data.IntegrationDbContext db, Yojana.Platform.Contracts.Tenancy.ITenantContext tenant) : IConnectionLifecycleAdapter
{
    public ConnectionRegistrationForm RegistrationForm => new(
        [new("service_uri", "Server address", "url"), new("company_database", "Company database", "text"),
         new("release", "Release", "text", "unknown"), new("transport", "Connection route", "route", "direct")],
        Cx01Client.CredentialPurpose, [new("user_name", "Dedicated integration user", "text"), new("password", "Password", "password")]);
    public string DefaultOutboundMode => "reference_only";
    public IReadOnlyList<string> CredentialPurposes => [Cx01Client.CredentialPurpose];
    public IReadOnlyList<string> RequiredCredentialPurposes(JsonElement value) =>
        Read(value) is { Transport: "site_relay", RelayCredentials: "site" } ? [] : CredentialPurposes;
    public void ValidateSettings(JsonElement value) => _ = Read(value);

    public async Task<ConnectionDeclaration> ConfigureAsync(Guid connectionId, JsonElement value, CancellationToken ct)
    {
        var previous = await settings.TryGetAsync(connectionId, ct);
        var incoming = Read(value);
        var configured = incoming with { DocumentsAcceptance = previous?.DocumentsAcceptance, Documents = incoming.Documents ?? previous?.Documents };
        var gateway = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(
            db.ConnectionGatewaySettings.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId), ct);
        if (gateway?.OutboundMode == "documents" && string.IsNullOrWhiteSpace(configured.Documents?.LocalCurrencyCode))
            throw new Cx01DocumentRuleException("Documents mode requires the remote local currency code from the company survey or connection settings.");
        await settings.SetAsync(connectionId, configured, ct);
        var declaration = Cx01Capabilities.For(configured.Release, configured.WebhooksAvailable, gateway?.OutboundMode ?? DefaultOutboundMode);
        return new(declaration.Messages, declaration.Throttle, Cx01Protocol.Entities.ToDictionary(x => x.Key, x => x.Value.CanonicalMessage), []);
    }

    public async Task<ConnectionProbe> SmokeReadAsync(Guid connectionId, CancellationToken ct)
    {
        var probed = await client.ProbeReleaseAsync(connectionId, ct);
        var configured = await settings.GetAsync(connectionId, ct);
        await settings.SetAsync(connectionId, configured with
        {
            Release = probed.Release,
            WebhooksAvailable = probed.WebhooksAvailable,
        }, ct);
        var result = await survey.RunAsync(connectionId, new(), ct);
        return new(JsonSerializer.SerializeToElement(new
        {
            result.ObservedAt, result.Release, result.Webhooks, result.TrackingTableAllowlist,
            MandatoryFieldsWithoutDefault = result.UserFields.Count(field => field.MandatoryWithoutDefault),
            result.OwnerAnswers,
        }, IntegrationContracts.Json), result.Webhooks.ReleaseSupportsWebhooks);
    }

    public async Task<JsonElement> ReadSettingsAsync(Guid connectionId, CancellationToken ct) =>
        Cx01Settings.ToJson(await settings.GetAsync(connectionId, ct));

    public async Task<Uri?> ServiceUriAsync(Guid connectionId, CancellationToken ct) =>
        (await settings.GetAsync(connectionId, ct)).ServiceUri;

    public async Task<ConnectionProbe?> LatestProbeAsync(Guid connectionId, CancellationToken ct)
    {
        var result = await survey.LatestAsync(connectionId, ct);
        return result is null ? null : new(JsonSerializer.SerializeToElement(new
        {
            result.ObservedAt, result.Release, result.Webhooks, result.TrackingTableAllowlist,
            MandatoryFieldsWithoutDefault = result.UserFields.Count(field => field.MandatoryWithoutDefault),
            result.OwnerAnswers,
        }, IntegrationContracts.Json), result.Webhooks.ReleaseSupportsWebhooks);
    }

    public async Task UnsubscribeAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        if (configured.WebhooksAvailable) await notifications.UnsubscribeAsync(connectionId, ct);
    }

    public async Task<bool> SupportsNotificationControlAsync(Guid connectionId, CancellationToken ct) =>
        (await settings.GetAsync(connectionId, ct)).WebhooksAvailable;

    public async Task SuspendNotificationsAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        if (configured.WebhooksAvailable) await notifications.PauseAsync(connectionId, ct);
    }

    public async Task ResumeNotificationsAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        if (configured.WebhooksAvailable) await notifications.ResumeAsync(connectionId, ct);
    }

    private static Cx01ConnectionSettings Read(JsonElement value) =>
        (value.Deserialize<Cx01ConnectionSettings>(IntegrationContracts.Json)
            ?? throw new InvalidOperationException("CX01 settings are required.")).Validate();
}
