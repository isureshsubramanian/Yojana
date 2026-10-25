using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Yojana.Developer.Authentication;
using Yojana.Developer.Data;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Security;
using Yojana.Serialisation.Contracts;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Developer.PublicApi;

public sealed record PublicOperationUnit(Guid Id);
public sealed record PublicOperationUnitPage(IReadOnlyList<PublicOperationUnit> Items, string? Next);

public static class PublicApiEndpoints
{
    public static RouteGroupBuilder MapPublicApiEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup(DeveloperEndpoints.PublicPrefix).AddEndpointFilter<PublicApiDeprecationFilter>();

        root.MapPost("/token", IssueTokenAsync).AllowAnonymous()
            .WithSummary("Exchange API client credentials for a one-hour opaque access token.")
            .Produces<TokenResponse>();

        var api = root.MapGroup(string.Empty).RequireAuthorization(PublicApiAuthentication.Policy)
            .RequireRateLimiting(PublicApiRateLimiting.ClientPolicy);
        api.MapGet("/units/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await ReadOrNotFound(await reads.UnitAsync(id, PublicApiHttp.CustomerParty(user), ct),
                PublicResource.Unit, user, reads, http, ct))
            .RequirePublicScope(PublicApiScopes.UnitsRead).WithSummary("Get a unit, identifiers, lineage and as-built.")
            .Produces<PublicApiUnit>();
        api.MapGet("/units", async (string identifier, string? after, int? limit, ClaimsPrincipal user,
                HttpContext http, IPublicApiReadPort reads, CancellationToken ct) =>
            await PublicApiHttp.ReadAsync(await reads.UnitsByIdentifierAsync(identifier, after, limit ?? 50,
                    PublicApiHttp.CustomerParty(user), ct), PublicResource.Unit, PublicApiHttp.Tier(user), reads,
                http, ct))
            .RequirePublicScope(PublicApiScopes.UnitsRead).WithSummary("Find units by any identifier.")
            .Produces<PublicApiPage<PublicApiUnit>>();
        api.MapGet("/units/{id:guid}/cover", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await ReadOrNotFound(await reads.CoverAsync(id, PublicApiHttp.CustomerParty(user), ct),
                PublicResource.Cover, user, reads, http, ct))
            .RequirePublicScope(PublicApiScopes.CoverRead).WithSummary("Get unit cover and covered records.")
            .Produces<PublicApiUnitCover>();

        api.MapGet("/claims/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await ReadOrNotFound(await reads.ClaimAsync(id, PublicApiHttp.CustomerParty(user), ct),
                PublicResource.Claim, user, reads, http, ct))
            .RequirePublicScope(PublicApiScopes.ClaimsRead).WithSummary("Get a claim.")
            .Produces<PublicApiClaim>();
        api.MapGet("/claims", async (Guid unit, string? after, int? limit, ClaimsPrincipal user,
                HttpContext http, IPublicApiReadPort reads, CancellationToken ct) =>
            await PublicApiHttp.ReadAsync(await reads.ClaimsAsync(unit, after, limit ?? 50,
                    PublicApiHttp.CustomerParty(user), ct), PublicResource.Claim, PublicApiHttp.Tier(user), reads,
                http, ct))
            .RequirePublicScope(PublicApiScopes.ClaimsRead).WithSummary("List claims for a unit.")
            .Produces<PublicApiPage<PublicApiClaim>>();

        api.MapGet("/rmas/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await ReadOrNotFound(await reads.RmaAsync(id, PublicApiHttp.CustomerParty(user), ct),
                PublicResource.Rma, user, reads, http, ct))
            .RequirePublicScope(PublicApiScopes.RmaRead).WithSummary("Get an RMA.")
            .Produces<PublicApiRma>();
        api.MapGet("/rmas", async (Guid unit, string? after, int? limit, ClaimsPrincipal user,
                HttpContext http, IPublicApiReadPort reads, CancellationToken ct) =>
            await PublicApiHttp.ReadAsync(await reads.RmasAsync(unit, after, limit ?? 50,
                    PublicApiHttp.CustomerParty(user), ct), PublicResource.Rma, PublicApiHttp.Tier(user), reads,
                http, ct))
            .RequirePublicScope(PublicApiScopes.RmaRead).WithSummary("List RMAs for a unit.")
            .Produces<PublicApiPage<PublicApiRma>>();
        api.MapGet("/replacements/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await ReadOrNotFound(await reads.ReplacementAsync(id, PublicApiHttp.CustomerParty(user), ct),
                PublicResource.Replacement, user, reads, http, ct))
            .RequirePublicScope(PublicApiScopes.RmaRead).WithSummary("Get a replacement.")
            .Produces<PublicApiReplacement>();
        api.MapGet("/replacements", async (Guid unit, string? after, int? limit, ClaimsPrincipal user,
                HttpContext http, IPublicApiReadPort reads, CancellationToken ct) =>
            await PublicApiHttp.ReadAsync(await reads.ReplacementsAsync(unit, after, limit ?? 50,
                    PublicApiHttp.CustomerParty(user), ct), PublicResource.Replacement,
                PublicApiHttp.Tier(user), reads, http, ct))
            .RequirePublicScope(PublicApiScopes.RmaRead).WithSummary("List replacements for a unit.")
            .Produces<PublicApiPage<PublicApiReplacement>>();

        api.MapGet("/certificates/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await ReadOrNotFound(await reads.CertificateAsync(id, PublicApiHttp.CustomerParty(user), ct),
                PublicResource.Certificate, user, reads, http, ct))
            .RequirePublicScope(PublicApiScopes.CertificatesRead).WithSummary("Get a certificate record.")
            .Produces<PublicApiCertificate>();
        api.MapGet("/certificates/{id:guid}/pdf", async (Guid id, ClaimsPrincipal user, HttpContext http,
                IPublicApiReadPort reads, CancellationToken ct) =>
            await reads.CertificatePdfAsync(id, PublicApiHttp.CustomerParty(user), ct) is { } document
                ? PublicApiHttp.File(document, http) : NotFound())
            .RequirePublicScope(PublicApiScopes.CertificatesRead).WithSummary("Download a certificate PDF.")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf");

        api.MapGet("/events/{id:guid}", EventAsync)
            .RequirePublicScope(PublicApiScopes.EventsRead).WithSummary("Get one public event envelope.")
            .Produces<JsonObject>();
        api.MapGet("/operations/{id:guid}/units", OperationUnitsAsync)
            .RequirePublicScope(PublicApiScopes.UnitsRead).WithSummary("List units affected by a bulk operation.")
            .Produces<PublicOperationUnitPage>();

        api.MapPost("/rmas", WriteRmaAsync).RequirePublicScope(PublicApiScopes.RmaWrite)
            .WithSummary("Request an RMA. Requires Idempotency-Key.").Produces<PublicRmaResult>(201);
        api.MapPost("/claims", WriteClaimAsync).RequirePublicScope(PublicApiScopes.ClaimsWrite)
            .WithSummary("Submit a claim with optional base64 evidence. Requires Idempotency-Key.")
            .Produces<PublicClaimResult>(201);
        api.MapPut("/units/{id:guid}/installation-date", WriteInstallationAsync)
            .RequirePublicScope(PublicApiScopes.UnitsWriteInstallation)
            .WithSummary("Submit installation start-date evidence. Requires Idempotency-Key.")
            .Produces<PublicInstallationDateResult>();
        api.MapPost("/units/{id:guid}/asset-tags", WriteAssetTagAsync)
            .RequirePublicScope(PublicApiScopes.UnitsWriteAssetTag)
            .WithSummary("Add a customer asset tag. Requires Idempotency-Key.")
            .Produces<PublicAssetTagResult>(201);
        return root;
    }

    private static async Task<IResult> IssueTokenAsync(HttpContext http, TokenService tokens,
        ApiAuthenticationRefusalLimiter refusalLimiter,
        CancellationToken cancellationToken)
    {
        using var attempt = refusalLimiter.Acquire(http.Connection.RemoteIpAddress?.ToString());
        if (!attempt.Allowed)
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(attempt.RetryAfter.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return PublicApiHttp.Problem("api_auth_throttled",
                "Too many API authentication refusals came from this address.",
                StatusCodes.Status429TooManyRequests);
        }
        try
        {
            TokenRequest? request;
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(cancellationToken);
                request = new(form["grant_type"].ToString(), form["client_id"].ToString(),
                    form["client_secret"].ToString(), form["scope"].ToString() is { Length: > 0 } scope
                        ? scope : null);
            }
            else if (http.Request.ContentType?.StartsWith("application/json",
                         StringComparison.OrdinalIgnoreCase) == true)
                request = await http.Request.ReadFromJsonAsync<TokenRequest>(cancellationToken);
            else
                return await TokenRefusalAsync(tokens, http, "token_content_type_unsupported",
                    "Use application/json or application/x-www-form-urlencoded.",
                    StatusCodes.Status415UnsupportedMediaType, attempt, cancellationToken);
            if (request is null) return await TokenRefusalAsync(tokens, http, "token_request_invalid",
                "The token request is required.", StatusCodes.Status400BadRequest, attempt, cancellationToken);
            return Results.Ok(await tokens.IssueAsync(request, Request(http), attempt, cancellationToken));
        }
        catch (TokenRefusedException exception)
        {
            return PublicApiHttp.Problem(exception.Code, exception.Message, exception.StatusCode);
        }
        catch (JsonException)
        {
            return await TokenRefusalAsync(tokens, http, "token_request_invalid",
                "The token request JSON is invalid.", StatusCodes.Status400BadRequest, attempt,
                cancellationToken);
        }
    }

    private static async Task<IResult> TokenRefusalAsync(TokenService tokens, HttpContext http, string code,
        string detail, int status, ApiAuthenticationRefusalLimiter.ApiAuthenticationAttempt attempt,
        CancellationToken cancellationToken)
    {
        await tokens.RecordRefusalAsync(code, Request(http), attempt, cancellationToken);
        return PublicApiHttp.Problem(code, detail, status);
    }

    private static async Task<IResult> EventAsync(Guid id, ClaimsPrincipal user, HttpContext http,
        DeveloperDbContext db, IPublicApiReadPort reads, CancellationToken cancellationToken)
    {
        var customer = PublicApiHttp.CustomerParty(user);
        var sandbox = PublicApiHttp.Sandbox(user);
        var row = await db.PublicEvents.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == db.CurrentTenantId
            && item.EventId == id && item.Test == sandbox
            && (customer == null || item.CustomerPartyRef == customer), cancellationToken);
        if (row is null) return NotFound();
        var body = JsonNode.Parse(row.Body) ?? throw new InvalidOperationException("Stored public event JSON is empty.");
        return await PublicApiHttp.ReadAsync(body, PublicResource.Event, PublicApiHttp.Tier(user), reads, http,
            cancellationToken);
    }

    private static async Task<IResult> OperationUnitsAsync(Guid id, string? after, int? limit,
        ClaimsPrincipal user, HttpContext http, DeveloperDbContext db, IPublicApiReadPort reads,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        if (!await db.BulkOperations.AsNoTracking().AnyAsync(row => row.TenantId == db.CurrentTenantId
                && row.OperationId == id, cancellationToken)) return NotFound();
        var cursor = DecodeGuid(after);
        var query = db.BulkOperationUnits.AsNoTracking().Where(row => row.TenantId == db.CurrentTenantId
            && row.OperationId == id);
        if (cursor is { } value) query = query.Where(row => row.UnitId.CompareTo(value) > 0);
        var ids = await query.OrderBy(row => row.UnitId).Select(row => row.UnitId).Take(take + 1)
            .ToListAsync(cancellationToken);
        var page = ids.Take(take).ToList();
        var customer = PublicApiHttp.CustomerParty(user);
        var visibleSet = customer is null ? null
            : await reads.VisibleUnitIdsAsync(page, customer, cancellationToken);
        var visible = visibleSet is null ? page : page.Where(visibleSet.Contains).ToList();
        if (customer is not null && visible.Count == 0) return NotFound();
        var response = new PublicOperationUnitPage(visible.Select(unitId => new PublicOperationUnit(unitId)).ToList(),
            ids.Count > take && page.Count > 0 ? EncodeGuid(page[^1]) : null);
        return await PublicApiHttp.ReadAsync(response, PublicResource.Operation, PublicApiHttp.Tier(user), reads,
            http, cancellationToken);
    }

    private static Task<IResult> WriteRmaAsync(PublicRmaRequest request, ClaimsPrincipal user, HttpContext http,
        IPublicApiWritePort writes, IdempotencyService idempotency, IClock clock, CancellationToken ct) =>
        GuardWrite(async () =>
        await idempotency.ExecuteAsync(http, PublicApiHttp.ClientGuid(user), "rmas.create", request,
            async commandId =>
            {
                var result = await writes.RequestRmaAsync(commandId, request, PublicApiHttp.CustomerParty(user),
                    Actor(user), clock.UtcNow, ct);
                if (result is null) throw new PublicResourceNotFoundException();
                return (StatusCodes.Status201Created, result);
            }, ct));

    private static Task<IResult> WriteClaimAsync(PublicClaimRequest request, ClaimsPrincipal user, HttpContext http,
        IPublicApiWritePort writes, IdempotencyService idempotency, IClock clock, CancellationToken ct) =>
        GuardWrite(async () =>
        await idempotency.ExecuteAsync(http, PublicApiHttp.ClientGuid(user), "claims.create", request,
            async commandId =>
            {
                var result = await writes.SubmitClaimAsync(commandId, request, PublicApiHttp.CustomerParty(user),
                    Actor(user), clock.UtcNow, ct);
                if (result is null) throw new PublicResourceNotFoundException();
                return (StatusCodes.Status201Created, result);
            }, ct));

    private static Task<IResult> WriteInstallationAsync(Guid id, PublicInstallationDateRequest request,
        ClaimsPrincipal user, HttpContext http, IPublicApiWritePort writes, IdempotencyService idempotency,
        CancellationToken ct) => GuardWrite(async () => await idempotency.ExecuteAsync(http,
            PublicApiHttp.ClientGuid(user), $"units.{id:N}.installation-date", request, async commandId =>
            {
                var result = await writes.SetInstallationDateAsync(commandId, id, request,
                    PublicApiHttp.CustomerParty(user), Actor(user), ct);
                if (result is null) throw new PublicResourceNotFoundException();
                return (StatusCodes.Status200OK, result);
            }, ct));

    private static Task<IResult> WriteAssetTagAsync(Guid id, PublicAssetTagRequest request,
        ClaimsPrincipal user, HttpContext http, IPublicApiWritePort writes, IdempotencyService idempotency,
        IClock clock, CancellationToken ct) => GuardWrite(async () => await idempotency.ExecuteAsync(http,
            PublicApiHttp.ClientGuid(user), $"units.{id:N}.asset-tags", request, async commandId =>
            {
                var result = await writes.AddAssetTagAsync(commandId, id, request,
                    PublicApiHttp.CustomerParty(user), Actor(user), clock.UtcNow, ct);
                if (result is null) throw new PublicResourceNotFoundException();
                return (StatusCodes.Status201Created, result);
            }, ct));

    private static async Task<IResult> GuardWrite(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (PublicResourceNotFoundException) { return NotFound(); }
        catch (CommandRefusedException exception)
        {
            return PublicApiHttp.Problem(exception.Rule, exception.Message);
        }
    }

    private static async Task<IResult> ReadOrNotFound<T>(T? value, PublicResource resource,
        ClaimsPrincipal user, IPublicApiReadPort reads, HttpContext http, CancellationToken cancellationToken)
        where T : class => value is null ? NotFound() : await PublicApiHttp.ReadAsync(value, resource,
            PublicApiHttp.Tier(user), reads, http, cancellationToken);

    private static IResult NotFound() => PublicApiHttp.Problem("not_found", "The requested resource was not found.",
        StatusCodes.Status404NotFound);

    private static string Actor(ClaimsPrincipal user) => "system:api-client:" + PublicApiHttp.ClientId(user);
    private static SecurityRequestContext Request(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);
    private static string EncodeGuid(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');
    private static Guid? DecodeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(padded);
            return bytes.Length == 16 ? new Guid(bytes) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed class PublicResourceNotFoundException : Exception;
}
