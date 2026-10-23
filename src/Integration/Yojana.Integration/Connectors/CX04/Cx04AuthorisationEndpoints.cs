using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Connectors.CX04;

public static class Cx04AuthorisationEndpoints
{
    public static IEndpointRouteBuilder MapCx04AuthorisationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration/connections/{connectionId:guid}/cx04")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        group.MapGet("/authorisation", (Guid connectionId, Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.ReadAsync(connectionId, ct)));
        group.MapPost("/consent", (Guid connectionId, Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.BeginAsync(connectionId, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/callback", (Guid connectionId, Cx04ConsentCallback request, ClaimsPrincipal user,
            Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.CompleteAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/disable", (Guid connectionId, ClaimsPrincipal user, Cx04Authorisation service,
            CancellationToken ct) => Invoke(() => service.DisableAsync(connectionId, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        return app;
    }

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (Cx04AuthorisationException exception)
        {
            var status = exception.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422;
            return Results.Problem(title: exception.Rule, detail: exception.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = exception.Rule });
        }
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new Cx04AuthorisationException("audit_actor_required",
            "The authenticated user has no actor id.");
}
