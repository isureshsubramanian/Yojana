using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Connections;

public static class ExtensionStatusEndpoints
{
    public static IEndpointRouteBuilder MapExtensionStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration/connections/{connectionId:guid}/extension")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        group.MapGet("", (Guid connectionId, ExtensionStatusService service, CancellationToken ct) =>
            Invoke(() => service.ReadAsync(connectionId, ct)));
        group.MapPost("/check", (Guid connectionId, ClaimsPrincipal user, ExtensionStatusService service,
            CancellationToken ct) => Invoke(() => service.CheckAsync(connectionId, Actor(user), ct)));
        group.MapGet("/installation-guide", async (Guid connectionId, ExtensionStatusService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Text(await service.InstallationGuideAsync(connectionId, ct),
                    "text/markdown", Encoding.UTF8);
            }
            catch (CommandRefusedException exception) { return Problem(exception); }
        });
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new ExtensionStatusRuleException("audit_actor_required",
            "The authenticated user has no actor id.");

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException exception) { return Problem(exception); }
    }

    private static IResult Problem(CommandRefusedException exception)
    {
        var status = exception.Rule.EndsWith("not_found", StringComparison.Ordinal)
            ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
        return Results.Problem(title: exception.Rule, detail: exception.Message, statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["rule"] = exception.Rule,
                ["unblockingStep"] = exception.UnblockingStep,
            });
    }
}
