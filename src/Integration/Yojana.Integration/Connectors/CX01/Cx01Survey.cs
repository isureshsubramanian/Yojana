using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01SurveyOwnerAnswers(
    bool? ValidationProcedureReviewed = null,
    string? ValidationProcedureNotes = null,
    bool? ScreenOnlyAddOnValidations = null,
    string? ScreenOnlyAddOnNotes = null);

public sealed record Cx01SurveyUserField(string TableName, string Name, string Type, string Mandatory,
    string? DefaultValue, bool MandatoryWithoutDefault, bool WrittenObject, int? Size = null);

public sealed record Cx01SurveyWebhook(
    bool ReleaseSupportsWebhooks,
    bool Enabled,
    int? RetryTimes,
    int? RetryInterval,
    int? RequestTimeout,
    int? MaximumSubscriptions,
    int SubscriptionCount,
    string EnablementFailureExplanation);

public sealed record Cx01TrackingTablePermission(string State, string Source, DateTime? ObservedAt = null);

public interface ICx01TrackingPermissionSink
{
    Task RecordDeniedAsync(Guid connectionId, string remoteMessage, CancellationToken ct);
}

public static class Cx01TrackingPermissions
{
    public static IReadOnlyList<string> Tables { get; } = ["OITL", "ITL1", "OSRN"];
    public static IReadOnlyDictionary<string, Cx01TrackingTablePermission> Declared(Cx01ConnectionSettings settings) =>
        Tables.ToDictionary(table => table, table =>
            settings.TrackingTableDeclarations?.FirstOrDefault(pair =>
                StringComparer.OrdinalIgnoreCase.Equals(pair.Key, table)) is { Key: not null } declared
                ? new Cx01TrackingTablePermission(declared.Value ? "allowed" : "not_allowed", "owner_declared")
                : settings.AllowlistedTrackingTables?.Contains(table, StringComparer.OrdinalIgnoreCase) == true
                    ? new("allowed", "owner_declared") : new("unknown", "unknown"), StringComparer.Ordinal);

    public static IReadOnlyList<string> DeniedTables(string message) => Tables.Where(table =>
        System.Text.RegularExpressions.Regex.IsMatch(message, @"(?<![A-Za-z0-9_])" + table + @"(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        .ToList();
}

public sealed record Cx01SurveyResult(
    Guid ConnectionId,
    DateTime ObservedAt,
    string Release,
    Cx01SurveyWebhook Webhooks,
    IReadOnlyList<Cx01SurveyUserField> UserFields,
    IReadOnlyDictionary<string, Cx01TrackingTablePermission> TrackingTableAllowlist,
    Cx01SurveyOwnerAnswers OwnerAnswers,
    // Null when not read (older survey, or the integration user may not read approval templates).
    IReadOnlyList<Cx01SurveyApprovalTemplate>? ApprovalTemplates = null, string? LocalCurrencyCode = null,
    IReadOnlyDictionary<string, int>? FieldLengths = null);

public sealed record Cx01SurveyPage(
    Guid ConnectionId,
    DateTime ObservedAt,
    string Release,
    Cx01SurveyWebhook Webhooks,
    IReadOnlyDictionary<string, Cx01TrackingTablePermission> TrackingTableAllowlist,
    Cx01SurveyOwnerAnswers OwnerAnswers,
    IReadOnlyList<Cx01SurveyUserField> UserFields,
    string? NextCursor);

/// <summary>Read-only discovery. Remote mutations are deliberately owned by separate onboarding steps.</summary>
public interface ICx01SurveyReader
{
    Task<IReadOnlyList<string>> MandatoryWithoutDefaultFieldsAsync(Guid connectionId, string table,
        CancellationToken cancellationToken);
    Task<int?> FieldLengthAsync(Guid connectionId, string collection, string field, CancellationToken ct) => Task.FromResult<int?>(null);
}

public interface ICx01SurveyRemotePort
{
    Task<string> ReleaseAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<string> GetTextAsync(Guid connectionId, string path, CancellationToken cancellationToken);
    Task<JsonDocument> GetAsync(Guid connectionId, string path, CancellationToken cancellationToken);
    Task<JsonDocument?> PostAsync(Guid connectionId, string path, CancellationToken cancellationToken);
}

internal sealed class Cx01SurveyRemotePort(Cx01Client client) : ICx01SurveyRemotePort
{
    public Task<string> ReleaseAsync(Guid connectionId, CancellationToken cancellationToken) =>
        client.ReleaseAsync(connectionId, cancellationToken);

    public Task<string> GetTextAsync(Guid connectionId, string path, CancellationToken cancellationToken) =>
        client.GetTextAsync(connectionId, path, cancellationToken);

    public Task<JsonDocument> GetAsync(Guid connectionId, string path, CancellationToken cancellationToken) =>
        client.GetAsync(connectionId, path, cancellationToken);

    public Task<JsonDocument?> PostAsync(Guid connectionId, string path, CancellationToken cancellationToken) =>
        client.SendJsonAsync(connectionId, HttpMethod.Post, path, new { }, true, cancellationToken);
}

/// <summary>The read-only remote survey can run without application persistence, including in live fact tests.</summary>
public sealed class Cx01SurveyProbe(ICx01SurveyRemotePort remote, IClock clock)
{
    public async Task<Cx01SurveyResult> ReadAsync(Guid connectionId,
        Cx01ConnectionSettings configured, Cx01SurveyOwnerAnswers answers, CancellationToken cancellationToken)
    {
        Cx01ReferenceRules.Validate(configured);
        var release = await remote.ReleaseAsync(connectionId, cancellationToken);
        var metadata = await remote.GetTextAsync(connectionId, Cx01Protocol.Metadata, cancellationToken);
        var releaseSupports = metadata.Contains(Cx01Protocol.EventSubscriptions, StringComparison.Ordinal);
        using var admin = await remote.PostAsync(connectionId, Cx01Protocol.Root + Cx01Protocol.AdminInfo,
            cancellationToken);
        var adminRoot = admin?.RootElement ?? default;
        var webhookEnabled = Text(adminRoot, "EnableWebhook") is "tYES" or "Y";
        var subscriptionCount = releaseSupports
            ? await SubscriptionCountAsync(connectionId, cancellationToken) : 0;
        var userFields = await UserFieldsAsync(connectionId, cancellationToken);
        var allowlist = Cx01TrackingPermissions.Declared(configured);
        var approvalTemplates = await ApprovalTemplatesAsync(connectionId, cancellationToken);
        return new(connectionId, clock.UtcNow, release,
            new(releaseSupports, webhookEnabled, Int(adminRoot, "WebhookRetryTimes"),
                Int(adminRoot, "WebhookRetryInterval"), Int(adminRoot, "WebhookRequestTimeout"),
                Int(adminRoot, "MaxNumberOfWebHooks"), subscriptionCount,
                "Error 10001237 while enabling webhooks means a configured company folder path is invalid; "
                + "the customer's administrator must correct it. The adapter does not change company settings."),
            userFields, allowlist, answers, approvalTemplates, Text(adminRoot, "LocalCurrency"), ReadLengths(metadata, userFields));
    }

    private static Dictionary<string, int> ReadLengths(string metadata, IReadOnlyList<Cx01SurveyUserField> fields)
    {
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var xml = System.Xml.Linq.XDocument.Parse(metadata);
            var types = xml.Descendants().Where(x => x.Name.LocalName == "EntityType")
                .ToDictionary(x => (string)x.Attribute("Name")!, x => x);
            foreach (var set in xml.Descendants().Where(x => x.Name.LocalName == "EntitySet"))
            {
                var collection = (string?)set.Attribute("Name");
                var type = ((string?)set.Attribute("EntityType"))?.Split('.').Last();
                if (collection is null || type is null || !types.TryGetValue(type, out var entity)) continue;
                foreach (var property in entity.Elements().Where(x => x.Name.LocalName == "Property"))
                    if (int.TryParse((string?)property.Attribute("MaxLength"), out var size))
                        lengths[collection + "." + (string?)property.Attribute("Name")] = size;
            }
        }
        catch (System.Xml.XmlException) { /* Some older endpoints return only a metadata capability summary. */ }
        foreach (var field in fields.Where(x => x.Size is > 0))
            foreach (var collection in Cx01Protocol.TableByCollection.Where(x => x.Value == field.TableName).Select(x => x.Key))
                lengths[collection + ".U_" + (field.Name.StartsWith("U_", StringComparison.Ordinal) ? field.Name[2..] : field.Name)] = field.Size!.Value;
        return lengths;
    }

    private async Task<IReadOnlyList<Cx01SurveyApprovalTemplate>?> ApprovalTemplatesAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var result = new List<Cx01SurveyApprovalTemplate>();
        var path = Cx01Protocol.Root + Cx01Protocol.ApprovalTemplates
            + "?$select=Code,Name,IsActive,ApprovalTemplateDocuments&$top=1000";
        try
        {
            while (path is not null)
            {
                using var response = await remote.GetAsync(connectionId, path, cancellationToken);
                foreach (var row in Values(response.RootElement))
                {
                    var documents = row.TryGetProperty("ApprovalTemplateDocuments", out var list)
                        && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Select(x => Text(x, "DocumentType")).OfType<string>().ToList()
                        : [];
                    result.Add(new(Int(row, "Code") ?? 0, Text(row, "Name") ?? "",
                        Text(row, "IsActive") is "tYES" or "Y", documents));
                }
                path = Cx01Protocol.NextPage(response.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString() : null);
            }
        }
        catch (Cx01RemoteException exception) when (exception.IsBusinessRejection)
        {
            // A restricted integration user may not read approval templates; policy then treats them as unknown.
            return null;
        }
        return result;
    }

    private async Task<int> SubscriptionCountAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var path = Cx01Protocol.Root + Cx01Protocol.EventSubscriptions + "?$select=WebhookID&$top=1000";
        while (path is not null)
        {
            using var response = await remote.GetAsync(connectionId, path, cancellationToken);
            count += Values(response.RootElement).Count;
            path = Cx01Protocol.NextPage(response.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString() : null);
        }
        return count;
    }

    private async Task<IReadOnlyList<Cx01SurveyUserField>> UserFieldsAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var tables = Cx01Protocol.TableByCollection.Values.ToHashSet(StringComparer.Ordinal);
        var written = new[] { "Returns", "CustomerEquipmentCards" }
            .Select(collection => Cx01Protocol.TableByCollection[collection]).ToHashSet(StringComparer.Ordinal);
        var result = new List<Cx01SurveyUserField>();
        var path = Cx01Protocol.Root + Cx01Protocol.UserFields
            + "?$select=TableName,Name,Type,Mandatory,DefaultValue,Size&$top=1000";
        while (path is not null)
        {
            using var response = await remote.GetAsync(connectionId, path, cancellationToken);
            foreach (var row in Values(response.RootElement))
            {
                var table = Text(row, "TableName") ?? "";
                if (!tables.Contains(table)) continue;
                var mandatory = Text(row, "Mandatory") ?? "tNO";
                var defaultValue = Text(row, "DefaultValue");
                result.Add(new(table, Text(row, "Name") ?? "", Text(row, "Type") ?? "",
                    mandatory, defaultValue, mandatory is "tYES" or "Y"
                    && string.IsNullOrWhiteSpace(defaultValue), written.Contains(table), Int(row, "Size")));
            }
            path = Cx01Protocol.NextPage(response.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString() : null);
        }
        return result;
    }

    private static List<JsonElement> Values(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var values)
            && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().ToList() : [];

    private static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;

    private static int? Int(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var found) && found.TryGetInt32(out var number) ? number : null;
}

public sealed class Cx01SurveyService(
    IntegrationDbContext db,
    ITenantContext tenant,
    Cx01Settings settings,
    Cx01SurveyProbe probe,
    IClock clock) : ICx01SurveyReader, ICx01TrackingPermissionSink
{
    private static readonly JsonSerializerOptions Json = new(IntegrationContracts.Json) { DictionaryKeyPolicy = null };

    public async Task<Cx01SurveyResult> RunAsync(Guid connectionId, Cx01SurveyOwnerAnswers answers,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var result = await probe.ReadAsync(connectionId, configured, answers,
            cancellationToken);
        if (configured.Documents?.LocalCurrencyCode is null && result.LocalCurrencyCode is { } local)
            await settings.SetAsync(connectionId, configured with { Documents = (configured.Documents ?? new()) with { LocalCurrencyCode = local } }, cancellationToken);
        var row = await db.Cx01SurveyRecords.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, cancellationToken);
        if (row is null)
        {
            row = new Cx01SurveyRecord
            {
                TenantId = tenant.TenantId,
                ConnectionId = connectionId,
                ObservedAt = result.ObservedAt,
                ResultJson = "{}",
            };
            db.Cx01SurveyRecords.Add(row);
        }
        var previous = row.ResultJson == "{}" ? null : ReadResult(row.ResultJson);
        if (previous is not null)
        {
            var merged = result.TrackingTableAllowlist.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var pair in previous.TrackingTableAllowlist.Where(pair => pair.Value.Source == "remote_table_denied"))
                merged[pair.Key] = pair.Value;
            result = result with { TrackingTableAllowlist = merged };
        }
        row.ObservedAt = result.ObservedAt;
        row.ResultJson = JsonSerializer.Serialize(result, Json);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<Cx01SurveyResult?> LatestAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var json = await db.Cx01SurveyRecords.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.ResultJson).SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : ReadResult(json);
    }

    public async Task RecordDeniedAsync(Guid connectionId, string remoteMessage, CancellationToken ct)
    {
        var denied = Cx01TrackingPermissions.DeniedTables(remoteMessage);
        if (denied.Count == 0) return;
        var row = await db.Cx01SurveyRecords.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct);
        if (row is null) return;
        var result = ReadResult(row.ResultJson);
        var merged = result.TrackingTableAllowlist.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var table in denied)
            merged[table] = new("not_allowed", "remote_table_denied", clock.UtcNow);
        row.ResultJson = JsonSerializer.Serialize(result with { TrackingTableAllowlist = merged }, Json);
        await db.SaveChangesAsync(ct);
    }

    // Earlier survey records stored false for absence, so false can only be migrated to unknown.
    // A legacy true retains its owner-declared evidence; no remote conclusion is invented during this upgrade.
    private static Cx01SurveyResult ReadResult(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        if (node["tracking_table_allowlist"] is System.Text.Json.Nodes.JsonObject values)
            foreach (var pair in values.ToList())
                if (pair.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var allowed))
                    values[pair.Key] = JsonSerializer.SerializeToNode(new Cx01TrackingTablePermission(
                        allowed ? "allowed" : "unknown", allowed ? "owner_declared" : "unknown"), IntegrationContracts.Json);
        var result = node.Deserialize<Cx01SurveyResult>(IntegrationContracts.Json)!;
        return result with { TrackingTableAllowlist = result.TrackingTableAllowlist
            .ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value, StringComparer.OrdinalIgnoreCase) };
    }

    public async Task<Cx01SurveyPage?> LatestPageAsync(Guid connectionId, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        var survey = await LatestAsync(connectionId, cancellationToken);
        if (survey is null) return null;
        take = Math.Clamp(take, 1, 200);
        var ordered = survey.UserFields.OrderBy(field => field.TableName, StringComparer.Ordinal)
            .ThenBy(field => field.Name, StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(cursor))
            ordered = ordered.Where(field => StringComparer.Ordinal.Compare(
                field.TableName + "|" + field.Name, cursor) > 0).ToList();
        var page = ordered.Take(take + 1).ToList();
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var next = hasMore && page.Count > 0 ? page[^1].TableName + "|" + page[^1].Name : null;
        return new(survey.ConnectionId, survey.ObservedAt, survey.Release, survey.Webhooks,
            survey.TrackingTableAllowlist, survey.OwnerAnswers, page, next);
    }

    public async Task<int?> FieldLengthAsync(Guid connectionId, string collection, string field, CancellationToken ct)
    {
        var latest = await LatestAsync(connectionId, ct);
        return latest?.FieldLengths?.GetValueOrDefault(collection + "." + field) is > 0 and var size ? size : null;
    }

    public async Task<IReadOnlyList<string>> MandatoryWithoutDefaultFieldsAsync(Guid connectionId, string table,
        CancellationToken cancellationToken)
    {
        var survey = await LatestAsync(connectionId, cancellationToken);
        return survey?.UserFields.Where(field => StringComparer.Ordinal.Equals(field.TableName, table)
                && field.MandatoryWithoutDefault).Select(field => field.Name).ToList() ?? [];
    }

}
