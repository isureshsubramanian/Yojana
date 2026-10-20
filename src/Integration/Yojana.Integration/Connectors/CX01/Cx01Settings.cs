using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01ConnectionSettings(
    Uri ServiceUri,
    string CompanyDatabase,
    string Release,
    bool WebhooksAvailable = false,
    bool SerialLinkViewEnabled = false,
    string? SerialLinkViewName = null,
    IReadOnlyList<string>? AllowlistedTrackingTables = null,
    string? RmaReferenceField = null,
    string? CertificateUrlField = null,
    string WebhookHmacAlgorithm = "HMACSHA256",
    bool WebhookVerifyCertificate = true,
    IReadOnlyList<Cx01WebhookEventSetting>? WebhookEvents = null,
    IReadOnlyDictionary<string, string>? MandatoryUserFieldDefaults = null,
    string OwnReferencePrefix = "YOJANA:",
    int PollPageSize = 100,
    int KeepAliveMinutes = 20,
    IReadOnlyDictionary<string, bool>? TrackingTableDeclarations = null,
    IReadOnlyDictionary<string, Cx01FieldSource>? FieldMap = null,
    IReadOnlyDictionary<string, Cx01ApprovalHandling>? ApprovalHandling = null)
{
    public Cx01ConnectionSettings Validate()
    {
        Cx01FieldMap.Validate(FieldMap);
        Cx01ApprovalPolicy.Validate(ApprovalHandling);
        if (!ServiceUri.IsAbsoluteUri || ServiceUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The CX01 service URL must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(CompanyDatabase))
            throw new InvalidOperationException("The CX01 company database is required.");
        if (PollPageSize is < 1 or > 1_000)
            throw new InvalidOperationException("The CX01 poll page size must be between 1 and 1000.");
        if (KeepAliveMinutes is < 1 or > 29)
            throw new InvalidOperationException("The CX01 keep-alive interval must be between 1 and 29 minutes.");
        if (SerialLinkViewEnabled && string.IsNullOrWhiteSpace(SerialLinkViewName))
            throw new InvalidOperationException("The enabled CX01 serial-link view needs its configured name.");
        if (WebhookHmacAlgorithm.ToUpperInvariant() is not ("HMACSHA256" or "HMACSHA384" or "HMACSHA512"))
            throw new InvalidOperationException("The CX01 webhook HMAC algorithm is not supported.");
        return this;
    }

    public IReadOnlyList<Cx01WebhookEventSetting> EffectiveWebhookEvents() => WebhookEvents is { Count: > 0 }
        ? WebhookEvents
        : Cx01Protocol.Entities.Values.SelectMany(entity => new[]
        {
            new Cx01WebhookEventSetting(entity.Collection, "Created", DefaultProperties(entity)),
            new Cx01WebhookEventSetting(entity.Collection, "Updated", DefaultProperties(entity)),
        }).ToList();

    private static IReadOnlyList<string> DefaultProperties(Cx01EntityDefinition entity) => entity.KeyField switch
    {
        "DocEntry" => ["DocEntry", "DocNum", "CardCode"],
        "ItemCode" => ["ItemCode"],
        "CardCode" => ["CardCode"],
        _ => [entity.KeyField],
    };
}

public sealed record Cx01WebhookEventSetting(string BusinessObject, string TransactionType,
    IReadOnlyList<string>? BizObjProps = null);

public sealed class Cx01Settings(ITenantSettings settings)
{
    // Dictionary keys in these settings are the customer's own field names and values (mandatory-field defaults,
    // field-map value translations). The contract options rewrite dictionary keys to snake case, which turned
    // "ORDN.U_RetCause" into "ordn.u_ret_cause" so it never matched again; keys must round-trip exactly.
    private static readonly JsonSerializerOptions Json = new(IntegrationContracts.Json) { DictionaryKeyPolicy = null };

    public static string Key(Guid connectionId) => $"integration.cx01.connection.{connectionId:N}";

    public async Task<Cx01ConnectionSettings> GetAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(Key(connectionId), cancellationToken)
            ?? throw new InvalidOperationException("CX01 connection settings are not configured.");
        return (JsonSerializer.Deserialize<Cx01ConnectionSettings>(value, Json)
            ?? throw new InvalidOperationException("CX01 connection settings are invalid.")).Validate();
    }

    public Task SetAsync(Guid connectionId, Cx01ConnectionSettings value, CancellationToken cancellationToken) =>
        settings.SetAsync(Key(connectionId), JsonSerializer.Serialize(value.Validate(), Json),
            cancellationToken);
}

internal sealed record Cx01Credential(string UserName, string Password);
