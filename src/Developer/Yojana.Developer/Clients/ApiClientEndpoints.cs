using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Security;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Developer.Clients;

public static class ApiClientEndpoints
{
    internal static readonly SortedPage<ApiClient, Guid> ApiClientPages = new("attention", row => row.ClientId,
        new CompoundSortColumn<ApiClient, int, DateTime>("attention",
            row => row.Status == ApiClientService.Active ? 0 : 1, row => row.CreatedAt,
            secondaryDirection: SortDirection.Descending),
        new SortColumn<ApiClient, string>("name", row => row.Name),
        new SortColumn<ApiClient, string>("status", row => row.Status));

    public static RouteGroupBuilder MapApiClientEndpoints(this RouteGroupBuilder screens)
    {
        var clients = screens.MapGroup("/clients");
        clients.MapGet("", async (string? sort, string? dir, string? cursor, int? take,
            ApiClientService service, CancellationToken ct) =>
            Results.Ok(await service.PageAsync(sort, dir, cursor, take, ct)));
        clients.MapGet("/{clientId}", GetAsync);
        clients.MapPost("", async (CreateApiClientRequest request, ClaimsPrincipal user, HttpContext http,
            ApiClientService service, CancellationToken ct) => await Guard(() => service.CreateAsync(request,
                Actor(user), Request(http), ct)));
        clients.MapPost("/{clientId}/rotate-secret", async (string clientId, ClaimsPrincipal user,
            HttpContext http, ApiClientService service, CancellationToken ct) => await Guard(() =>
                service.RotateAsync(clientId, Actor(user), Request(http), ct)));
        clients.MapPost("/{clientId}/revoke", async (string clientId, ClaimsPrincipal user, HttpContext http,
            ApiClientService service, CancellationToken ct) => await Guard(() => service.RevokeAsync(clientId,
                Actor(user), Request(http), ct)));
        clients.MapPut("/{clientId}", async (string clientId, UpdateApiClientRequest request,
            ClaimsPrincipal user, HttpContext http, ApiClientService service, CancellationToken ct) =>
            await Guard(() => service.UpdateAsync(clientId, request, Actor(user), Request(http), ct)));
        return screens;
    }

    private static async Task<IResult> Guard<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException exception)
        {
            return Results.Problem(title: exception.Rule, detail: exception.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Rule });
        }
    }

    private static async Task<IResult> GetAsync(string clientId, ApiClientService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.GetAsync(clientId, cancellationToken) is { } found
                ? Results.Ok(found) : Results.NotFound();
        }
        catch (CommandRefusedException exception)
        {
            return Results.Problem(title: exception.Rule, detail: exception.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Rule });
        }
    }

    private static Guid Actor(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(YojanaClaim.UserId) ?? throw new UnauthorizedAccessException());

    private static SecurityRequestContext Request(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);
}
