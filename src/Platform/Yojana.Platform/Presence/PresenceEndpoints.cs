using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Presence;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Presence;

public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        var presence = app.MapGroup("/api/presence").RequireAuthorization(YojanaPolicy.TenantMember);
        presence.MapPost("/heartbeat", async (PresenceRequest request, ClaimsPrincipal user,
            IPresenceService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var others = await service.HeartbeatAsync(UserId(user), request.DocumentType, request.DocumentId,
                    request.Mode, cancellationToken);
                return Results.Ok(new PresenceResponse(others));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "presence_invalid", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
        presence.MapDelete("/", async (LeavePresenceRequest request, ClaimsPrincipal user,
            IPresenceService service, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.LeaveAsync(UserId(user), request.DocumentType, request.DocumentId, cancellationToken);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "presence_invalid", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
        return app;
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirst(YojanaClaim.UserId)!.Value);
}
