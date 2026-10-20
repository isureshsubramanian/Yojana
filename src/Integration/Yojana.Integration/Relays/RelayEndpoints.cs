using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Platform.Contracts.Security;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Relays;

public static class RelayEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/integration/connections/{connectionId:guid}/relays")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        admin.MapPost("", (Guid connectionId, RegisterSiteRelay request, ClaimsPrincipal user,
            RelayRegistry registry, HttpResponse response, CancellationToken ct) => Admin(async () =>
            {
                response.Headers.CacheControl = "no-store";
                return await registry.RegisterAsync(connectionId, request, Actor(user), ct);
            })).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        admin.MapGet("", (Guid connectionId, string? cursor, int? take, RelayRegistry registry, CancellationToken ct) =>
            Admin(() => registry.ListAsync(connectionId, cursor, take ?? 50, ct)));
        admin.MapPost("/{relayId:guid}/rotate", (Guid connectionId, Guid relayId, RotateSiteRelay request,
            ClaimsPrincipal user, RelayRegistry registry, HttpResponse response, CancellationToken ct) => Admin(async () =>
            {
                response.Headers.CacheControl = "no-store";
                return await registry.RotateAsync(connectionId, relayId, request, Actor(user), ct);
            })).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        admin.MapPost("/{relayId:guid}/revoke", (Guid connectionId, Guid relayId,
            ClaimsPrincipal user, RelayRegistry registry, CancellationToken ct) =>
                Admin(() => registry.RevokeAsync(connectionId, relayId, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        var agent = app.MapGroup("/api/relay/v1").AllowAnonymous().RequireRateLimiting(RelayRateLimiting.PolicyName);
        agent.MapPost("/poll", (HttpContext http, IServiceScopeFactory scopes) => AgentAsync(http, scopes, "poll"));
        agent.MapPost("/results", (HttpContext http, IServiceScopeFactory scopes) => AgentAsync(http, scopes, "results"));
        agent.MapPost("/webhook", (HttpContext http, IServiceScopeFactory scopes) => AgentAsync(http, scopes, "webhook"));
        return app;
    }

    private static async Task<IResult> AgentAsync(HttpContext http, IServiceScopeFactory scopes, string operation)
    {
        var ct = http.RequestAborted;
        try
        {
            var maximum = operation == "poll" ? 16 * 1024 : RelayRequestQueue.WireLimit;
            var size = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = maximum;
            if (http.Request.ContentLength > maximum) return Results.StatusCode(413);
            var body = await RelayRequestQueue.ReadBoundedAsync(http.Request.Body, maximum, ct);
            // Agent authentication ignores browser cookies and claims. Tenant scope starts empty and is populated
            // solely by the signed relay lookup before the first SQL connection opens.
            using var scope = scopes.CreateScope();
            var relay = await scope.ServiceProvider.GetRequiredService<RelayAuthentication>().AuthenticateAsync(http.Request, body, ct);
            if (relay is null) return Results.Unauthorized();
            var queue = scope.ServiceProvider.GetRequiredService<RelayRequestQueue>();
            if (operation == "poll")
                return Results.Json(await queue.PollAsync(relay, Read<RelayPoll>(), ct), JsonSerializerOptions.Web);
            if (operation == "results")
                return Results.Json(await queue.CompleteAsync(relay, Read<RelayResult>(), ct), JsonSerializerOptions.Web);
            var (_, profile) = await scope.ServiceProvider.GetRequiredService<RelayRegistry>().ProfileAsync(relay.ConnectionId, ct);
            var webhook = Read<RelayWebhook>();
            if (webhook.Body.Length > RelayRequestQueue.BodyLimit || webhook.Headers.Count > 16
                || webhook.Query.Length > 16_384) return Results.StatusCode(413);
            return Results.Json(await profile.WebhookAsync(relay.ConnectionId, webhook, ct), JsonSerializerOptions.Web);

            T Read<T>() => JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)
                ?? throw new JsonException("relay_request_invalid");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Results.StatusCode(413); }
        catch (InvalidDataException) { return Results.StatusCode(413); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (ConnectionRuleException ex) { return Results.Json(new { error = ex.Rule }, statusCode: 401); }
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new ConnectionRuleException("audit_actor_required", "The authenticated user has no actor id.");

    private static async Task<IResult> Admin<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ConnectionRuleException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422);
        }
    }
}
