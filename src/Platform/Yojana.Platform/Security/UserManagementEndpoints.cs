using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Security;

public static class UserManagementEndpoints
{
    public static IEndpointRouteBuilder MapYojanaUserManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapGroup("/api/security/users")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        users.MapGet("", PageAsync);
        users.MapGet("/summary", SummaryAsync);
        users.MapGet("/{userId:guid}", DetailAsync);
        users.MapPut("/{userId:guid}/roles", ChangeRolesAsync);
        users.MapPost("/{userId:guid}/deactivate", DeactivateAsync);
        users.MapPost("/{userId:guid}/reactivate", ReactivateAsync);
        return endpoints;
    }

    private static async Task<IResult> PageAsync(string? status, string? role, string? q, string? cursor, int? take,
        UserManagementService service, CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.PageAsync(status, role, q, cursor, take ?? 50, cancellationToken)); }
        catch (UserManagementInputException exception) { return Problem(exception.Code, exception.Message, 400); }
    }

    private static async Task<IResult> SummaryAsync(UserManagementService service,
        CancellationToken cancellationToken) => Results.Ok(await service.SummaryAsync(cancellationToken));

    private static async Task<IResult> DetailAsync(Guid userId, UserManagementService service, HttpContext http,
        CancellationToken cancellationToken)
    {
        var detail = await service.DetailAsync(userId, UserId(http.User), cancellationToken);
        return detail is null ? Problem("user_not_found", "The user is not a staff member of this tenant.", 404) :
            Results.Ok(detail);
    }

    private static async Task<IResult> ChangeRolesAsync(Guid userId, ChangeRolesRequest request,
        UserManagementService service, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.ChangeRolesAsync(userId, request.Roles, request.Version,
                UserId(http.User), RequestContext(http), cancellationToken));
        }
        catch (UserNotFoundException) { return Problem("user_not_found", "The user is not a staff member of this tenant.", 404); }
        catch (UserManagementInputException exception) { return Problem(exception.Code, exception.Message, 400); }
        catch (LastAdministratorException) { return Problem("last_administrator", "The tenant must keep an active tenant administrator.", 409); }
        catch (UserVersionConflictException) { return Problem("version_conflict", "The membership changed after it was read.", 409); }
    }

    private static async Task<IResult> DeactivateAsync(Guid userId, MembershipReasonRequest request,
        UserManagementService service, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            await service.DeactivateAsync(userId, request.Reason, UserId(http.User), RequestContext(http),
                cancellationToken);
            return Results.NoContent();
        }
        catch (UserNotFoundException) { return Problem("user_not_found", "The user is not a staff member of this tenant.", 404); }
        catch (UserManagementInputException exception) { return Problem(exception.Code, exception.Message, 400); }
        catch (CannotDeactivateSelfException) { return Problem("cannot_deactivate_self", "You cannot deactivate your own membership.", 409); }
        catch (LastAdministratorException) { return Problem("last_administrator", "The tenant must keep an active tenant administrator.", 409); }
    }

    private static async Task<IResult> ReactivateAsync(Guid userId, UserManagementService service, HttpContext http,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.ReactivateAsync(userId, UserId(http.User), RequestContext(http), cancellationToken);
            return Results.NoContent();
        }
        catch (UserNotFoundException) { return Problem("user_not_found", "The user is not a staff member of this tenant.", 404); }
    }

    internal static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(YojanaClaim.UserId)!);
    internal static SecurityRequestContext RequestContext(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
    internal static IResult Problem(string code, string detail, int status) =>
        Results.Problem(title: code, detail: detail, statusCode: status);
}

public sealed record ChangeRolesRequest(IReadOnlyList<string> Roles, string Version);
public sealed record MembershipReasonRequest(string Reason);
