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
    string Transport = "direct", string RelayCredentials = "site", string? RelayWebhookUrl = null,
    int RelayOfflineAfterSeconds = 120, int RelayRequestTimeoutSeconds = 90, int RelayQueueCapacity = 128,
    IReadOnlyDictionary<string, Cx01ApprovalHandling>? ApprovalHandling = null,
    Cx01DocumentSettings? Documents = null, Cx01DocumentAcceptance? DocumentsAcceptance = null,
    Cx01NativeWarrantyStatusCodes? NativeWarrantyStatuses = null)
{
    public Cx01ConnectionSettings Validate()
    {
        Cx01FieldMap.Validate(FieldMap);
        Cx01ReferenceRules.Validate(this);
        Cx01ApprovalPolicy.Validate(ApprovalHandling);
        Documents?.Validate();
        NativeWarrantyStatuses?.Validate();
        if (RelayWebhookUrl is not null) Yojana.Relay.Contracts.RelayWebhookUrl.Validate(RelayWebhookUrl);
        if (Transport is not ("direct" or "site_relay") || RelayCredentials is not ("site" or "cloud")
            || RelayQueueCapacity is < 1 or > 1024 || RelayOfflineAfterSeconds is < 30 or > 3600 || RelayRequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("The relay transport settings are invalid.");
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

/// <summary>
/// Per-connection translation for remote status values that are not yet verified. A null value deliberately opts out
/// of that status and makes the writer use Active while exposing a connection-health note.
/// </summary>
public sealed record Cx01NativeWarrantyStatusCodes(
    string? InRepair = "sns_InRepair",
    string? Returned = "sns_Returned",
    string? Loaned = "sns_Loaned")
{
    public Cx01NativeWarrantyStatusCodes Validate()
    {
        if (new[] { InRepair, Returned, Loaned }.Any(value => value is not null && string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException("Configured CX01 native-warranty status codes must not be empty.");
        return this;
    }
}

public sealed record Cx01WebhookEventSetting(string BusinessObject, string TransactionType,
    IReadOnlyList<string>? BizObjProps = null);

public sealed class Cx01Settings(ITenantSettings settings)
{
    // Dictionary keys in these settings are the customer's own field names and values (mandatory-field defaults,
    // field-map value translations). The contract options rewrite dictionary keys to snake case, which turned
    // "ORDN.U_RetCause" into "ordn.u_ret_cause" so it never matched again; keys must round-trip exactly.
    private static readonly JsonSerializerOptions Json = new(IntegrationContracts.Json) { DictionaryKeyPolicy = null };

    public static JsonElement ToJson(Cx01ConnectionSettings value) => JsonSerializer.SerializeToElement(value, Json);

    public static string Key(Guid connectionId) => $"integration.cx01.connection.{connectionId:N}";

    public async Task<Cx01ConnectionSettings?> TryGetAsync(Guid connectionId, CancellationToken ct) =>
        await settings.GetAsync(Key(connectionId), ct) is { } value ? JsonSerializer.Deserialize<Cx01ConnectionSettings>(value, Json) : null;

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
