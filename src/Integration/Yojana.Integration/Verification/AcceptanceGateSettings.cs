using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Verification;

public sealed class AcceptanceGateSettings(ITenantSettings settings,
    ConnectorVerificationManifestStore manifests)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string SettingKey = "integration.acceptance.require_before_go_live";

    public async Task<AcceptanceGateSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var values = stored is null ? Defaults() : Read(stored.Value);
        return new(ToView(values), stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<bool> RequiredAsync(string connectorCode, CancellationToken cancellationToken)
    {
        var view = await GetAsync(cancellationToken);
        return view.Connectors.SingleOrDefault(row => row.ConnectorCode == connectorCode)?.Required ?? false;
    }

    public async Task<AcceptanceGateSettingsView> SetAsync(UpdateAcceptanceGateSettings request,
        CancellationToken cancellationToken)
    {
        var known = manifests.ConnectorCodes.ToHashSet(StringComparer.Ordinal);
        if (request.Connectors.Count != request.Connectors.Select(row => row.ConnectorCode)
                .Distinct(StringComparer.Ordinal).Count()
            || request.Connectors.Any(row => !known.Contains(row.ConnectorCode)))
            throw new ConnectorAcceptanceRuleException("acceptance_setting_connector_invalid",
                "Each connector may appear once and must have a shipped verification manifest.");
        var values = Defaults();
        foreach (var connector in request.Connectors) values[connector.ConnectorCode] = connector.Required;
        var saved = await settings.SetAsync(SettingKey,
            JsonSerializer.Serialize(values, Json), request.ExpectedVersion,
            cancellationToken);
        return new(ToView(values), saved.Version);
    }

    private Dictionary<string, bool> Defaults() => manifests.ConnectorCodes.ToDictionary(code => code,
        code => code is "CX02" or "CX04", StringComparer.Ordinal);

    private Dictionary<string, bool> Read(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, Json)
                ?? [];
            var values = Defaults();
            foreach (var pair in stored.Where(pair => values.ContainsKey(pair.Key))) values[pair.Key] = pair.Value;
            return values;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Tenant setting '{SettingKey}' is invalid.", exception);
        }
    }

    private static List<AcceptanceRequirement> ToView(IReadOnlyDictionary<string, bool> values) =>
        values.OrderBy(pair => pair.Key).Select(pair => new AcceptanceRequirement(pair.Key, pair.Value)).ToList();
}
