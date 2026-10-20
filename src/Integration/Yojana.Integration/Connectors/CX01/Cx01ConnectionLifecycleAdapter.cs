using System.Text.Json;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

public sealed class Cx01ConnectionLifecycleAdapter(Cx01Settings settings, Cx01Client client,
    Cx01SurveyService survey, Cx01WebhookMaintenance notifications) : IConnectionLifecycleAdapter
{
    public IReadOnlyList<string> CredentialPurposes => [Cx01Client.CredentialPurpose];
    public IReadOnlyList<string> RequiredCredentialPurposes(JsonElement value) =>
        Read(value) is { Transport: "site_relay", RelayCredentials: "site" } ? [] : CredentialPurposes;
    public void ValidateSettings(JsonElement value) => _ = Read(value);

    public async Task<ConnectionDeclaration> ConfigureAsync(Guid connectionId, JsonElement value, CancellationToken ct)
    {
        var configured = Read(value);
        await settings.SetAsync(connectionId, configured, ct);
        var declaration = Cx01Capabilities.For(configured.Release, configured.WebhooksAvailable);
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
        JsonSerializer.SerializeToElement(await settings.GetAsync(connectionId, ct), IntegrationContracts.Json);

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

    private static Cx01ConnectionSettings Read(JsonElement value) =>
        (value.Deserialize<Cx01ConnectionSettings>(IntegrationContracts.Json)
            ?? throw new InvalidOperationException("CX01 settings are required.")).Validate();
}
