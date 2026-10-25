using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.PublicApi;

public static class PublicApiHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ClientId(ClaimsPrincipal user) =>
        user.FindFirstValue(Authentication.PublicApiAuthentication.ClientIdClaim)
        ?? throw new UnauthorizedAccessException();

    public static Guid ClientGuid(ClaimsPrincipal user)
    {
        var value = ClientId(user);
        return Authentication.TokenService.TryClientId(value, out _, out var clientId)
            ? clientId : throw new UnauthorizedAccessException();
    }

    public static Guid? CustomerParty(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(YojanaClaim.CustomerPartyRef), out var value) ? value : null;

    public static string Tier(ClaimsPrincipal user) =>
        user.FindFirstValue(Authentication.PublicApiAuthentication.DisclosureTierClaim)
        ?? throw new UnauthorizedAccessException();

    public static bool Sandbox(ClaimsPrincipal user) => string.Equals(
        user.FindFirstValue(Authentication.PublicApiAuthentication.SandboxClaim), "true",
        StringComparison.Ordinal);

    public static IResult Problem(string code, string detail, int status = StatusCodes.Status422UnprocessableEntity) =>
        Results.Problem(title: code, detail: detail, statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "invalid_token",
        StatusCodes.Status403Forbidden => "access_denied",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status422UnprocessableEntity => "command_refused",
        StatusCodes.Status429TooManyRequests => "rate_limit_exceeded",
        StatusCodes.Status500InternalServerError => "internal_error",
        _ => "request_failed",
    };

    public static async Task<IResult> ReadAsync<T>(T value, PublicResource resource, string tier,
        IPublicApiReadPort reads, HttpContext http, CancellationToken cancellationToken)
    {
        var policy = await reads.DisclosureAsync(tier, cancellationToken);
        var node = JsonSerializer.SerializeToNode(value, Json)
            ?? throw new InvalidOperationException("A public API response could not be serialised.");
        Filter(node, resource, policy.VisibleFields);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node, Json);
        return WithEtag(bytes, "application/json; charset=utf-8", http);
    }

    public static IResult File(PublicApiDocument document, HttpContext http)
    {
        var result = WithEtag(document.Bytes, document.ContentType, http);
        if (result is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status304NotModified })
            http.Response.Headers.ContentDisposition = $"inline; filename=\"{document.FileName.Replace("\"", "", StringComparison.Ordinal)}\"";
        return result;
    }

    private static IResult WithEtag(byte[] bytes, string contentType, HttpContext http)
    {
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\"";
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(bytes, contentType);
    }

    private static void Filter(JsonNode node, PublicResource resource, IReadOnlySet<string> visible)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in Rules[resource])
                if (!visible.Contains(property.Value)) obj.Remove(property.Key);
            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>().ToList())
                Filter(child, resource, visible);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>()) Filter(child, resource, visible);
        }
    }

    private static readonly Dictionary<PublicResource, IReadOnlyDictionary<string, string>> Rules =
        new Dictionary<PublicResource, IReadOnlyDictionary<string, string>>
        {
            [PublicResource.Unit] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["value"] = PortalDisclosureField.IdentifierValue,
                ["type"] = PortalDisclosureField.IdentifierType,
                ["current"] = PortalDisclosureField.IdentifierLineage,
                ["replacedById"] = PortalDisclosureField.IdentifierLineage,
                ["itemCode"] = PortalDisclosureField.ItemCode,
                ["itemName"] = PortalDisclosureField.ItemName,
                ["condition"] = PortalDisclosureField.Condition,
                ["manufactureMonth"] = PortalDisclosureField.ManufactureMonth,
                ["deliveryDate"] = PortalDisclosureField.DeliveryDate,
                ["asBuilt"] = PortalDisclosureField.ServiceHistory,
            },
            [PublicResource.Cover] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = PortalDisclosureField.WarrantyStatus,
                ["startDate"] = PortalDisclosureField.CoverageExpiry,
                ["endDate"] = PortalDisclosureField.CoverageExpiry,
                ["ownerPartyId"] = PortalDisclosureField.PartyData,
                ["obligorEntityId"] = PortalDisclosureField.PartyData,
            },
            [PublicResource.Claim] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claimantPartyId"] = PortalDisclosureField.PartyData,
                ["settlementPartyId"] = PortalDisclosureField.PartyData,
                ["serviceEntityId"] = PortalDisclosureField.PartyData,
                ["obligorEntityId"] = PortalDisclosureField.PartyData,
                ["requestedAmount"] = PortalDisclosureField.ServiceCost,
                ["approvedAmount"] = PortalDisclosureField.ServiceCost,
                ["currency"] = PortalDisclosureField.ServiceCost,
                ["reportedDescription"] = PortalDisclosureField.ServiceInternalNotes,
                ["description"] = PortalDisclosureField.ServiceInternalNotes,
                ["reference"] = PortalDisclosureField.DocumentReferences,
            },
            [PublicResource.Rma] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["returningPartyId"] = PortalDisclosureField.PartyData,
                ["settlementPartyId"] = PortalDisclosureField.PartyData,
                ["serviceEntityId"] = PortalDisclosureField.PartyData,
                ["obligorEntityId"] = PortalDisclosureField.PartyData,
                ["requestedAmount"] = PortalDisclosureField.ServiceCost,
                ["approvedAmount"] = PortalDisclosureField.ServiceCost,
                ["currency"] = PortalDisclosureField.ServiceCost,
                ["description"] = PortalDisclosureField.ServiceInternalNotes,
            },
            [PublicResource.Replacement] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["counterpartyPartyId"] = PortalDisclosureField.PartyData,
            },
            [PublicResource.Certificate] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trackingNumber"] = PortalDisclosureField.Certificates,
                ["formType"] = PortalDisclosureField.Certificates,
                ["supersedesId"] = PortalDisclosureField.Certificates,
            },
            [PublicResource.Event] = new Dictionary<string, string>(StringComparer.Ordinal),
            [PublicResource.Operation] = new Dictionary<string, string>(StringComparer.Ordinal),
        };
}

public enum PublicResource { Unit, Cover, Claim, Rma, Replacement, Certificate, Event, Operation }
