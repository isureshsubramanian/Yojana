using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Yojana.Developer.Contracts;
using Yojana.Developer.Delivery;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Security;

namespace Yojana.Developer;

/// <summary>
/// Two route groups that must never share authentication (ADR-0018 D5): the settings screens under
/// <c>/api/developer</c> use the staff sign-in cookie; the public API under <c>/api/public/v1</c> accepts only an API
/// client's bearer token.
/// </summary>
public static class DeveloperEndpoints
{
    public const string ScreensPrefix = "/api/developer";
    public const string PublicPrefix = "/api/public/v1";

    public static IEndpointRouteBuilder MapDeveloperEndpoints(this IEndpointRouteBuilder app)
    {
        var screens = app.MapGroup(ScreensPrefix).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        // P14-A: tenant-administrator webhook management and delivery log API.
        var webhooks = screens.MapGroup("/webhooks");
        webhooks.MapGet("/", (string? after, int? take, WebhookSubscriptionService service,
            CancellationToken ct) => service.ListAsync(after, take, ct));
        webhooks.MapGet("/{id:guid}", async (Guid id, WebhookSubscriptionService service,
            CancellationToken ct) => await service.GetAsync(id, ct) is { } value
                ? Results.Ok(value) : Results.NotFound());
        webhooks.MapPost("/", async (CreateWebhookSubscription request, ClaimsPrincipal user,
            HttpContext http, WebhookSubscriptionService service, CancellationToken ct) =>
        {
            var response = await service.CreateAsync(request, Actor(user), Request(http), ct);
            return Results.Created($"{ScreensPrefix}/webhooks/{response.Subscription.Id:D}", response);
        });
        webhooks.MapPut("/{id:guid}", (Guid id, UpdateWebhookSubscription request, ClaimsPrincipal user,
            HttpContext http, WebhookSubscriptionService service, CancellationToken ct) =>
            service.UpdateAsync(id, request, Actor(user), Request(http), ct));
        webhooks.MapPost("/{id:guid}/rotate-secret", (Guid id, ClaimsPrincipal user, HttpContext http,
            WebhookSubscriptionService service, CancellationToken ct) =>
            service.RotateAsync(id, Actor(user), Request(http), ct));
        webhooks.MapPost("/{id:guid}/pause", (Guid id, ClaimsPrincipal user, HttpContext http,
            WebhookSubscriptionService service, CancellationToken ct) =>
            service.PauseAsync(id, Actor(user), Request(http), ct));
        webhooks.MapPost("/{id:guid}/resume", (Guid id, ClaimsPrincipal user, HttpContext http,
            WebhookSubscriptionService service, CancellationToken ct) =>
            service.ResumeAsync(id, Actor(user), Request(http), ct));
        webhooks.MapDelete("/{id:guid}", (Guid id, ClaimsPrincipal user, HttpContext http,
            WebhookSubscriptionService service, CancellationToken ct) =>
            service.DeleteAsync(id, Actor(user), Request(http), ct));
        webhooks.MapPost("/{id:guid}/verify", (Guid id, ClaimsPrincipal user, HttpContext http,
            WebhookSubscriptionService service, CancellationToken ct) =>
            service.VerifyAgainAsync(id, Actor(user), Request(http), ct));
        webhooks.MapPost("/{id:guid}/test", (Guid id, SendWebhookTestRequest request,
            WebhookSubscriptionService service, CancellationToken ct) => service.SendTestAsync(id, request, ct));
        webhooks.MapGet("/{id:guid}/deliveries", (Guid id, string? status, string? eventType,
            DateTime? from, DateTime? to, string? cursor, int? take, WebhookDeliveryQueries queries,
            CancellationToken ct) => queries.ListAsync(id, status, eventType, from, to, cursor, take, ct));
        webhooks.MapGet("/{id:guid}/deliveries/{deliveryId:guid}", async (Guid id, Guid deliveryId,
            WebhookDeliveryQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(id, deliveryId, ct) is { } value ? Results.Ok(value) : Results.NotFound());
        webhooks.MapPost("/{id:guid}/deliveries/{deliveryId:guid}/redeliver", (Guid id,
            Guid deliveryId, WebhookDeliveryQueries queries, CancellationToken ct) =>
            queries.RedeliverAsync(id, deliveryId, ct));
        webhooks.MapPost("/{id:guid}/deliveries/redeliver-range", (Guid id,
            RedeliverRangeRequest request, WebhookDeliveryQueries queries, CancellationToken ct) =>
            queries.RedeliverRangeAsync(id, request, ct));
        webhooks.MapPost("/{id:guid}/deliveries/{deliveryId:guid}/discard", (Guid id,
            Guid deliveryId, ClaimsPrincipal user, WebhookDeliveryQueries queries, CancellationToken ct) =>
            queries.DiscardAsync(id, deliveryId, Actor(user), ct));
        // End P14-A.
        return app;
    }

    private static Guid Actor(ClaimsPrincipal user) => Guid.TryParse(
        user.FindFirstValue(YojanaClaim.UserId), out var actor) ? actor
        : throw new UnauthorizedAccessException("The signed-in user id claim is missing.");

    private static SecurityRequestContext Request(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}
