using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Security;

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapYojanaInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var invitations = endpoints.MapGroup("/api/security/invitations")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        invitations.MapPost("", CreateAsync);
        invitations.MapPost("/{invitationId:guid}/resend", ResendAsync);
        invitations.MapDelete("/{invitationId:guid}", RevokeAsync);

        var auth = endpoints.MapGroup("/api/auth/invitations");
        auth.MapPost("/inspect", InspectAsync).AllowAnonymous().RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/accept", AcceptAsync).AllowAnonymous().RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/accept-signed-in", AcceptSignedInAsync).RequireAuthorization()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(CreateInvitationRequest request, InvitationService service,
        HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var created = await service.CreateAsync(request.Email, request.DisplayName, request.Roles,
                UserManagementEndpoints.UserId(http.User), UserManagementEndpoints.RequestContext(http), cancellationToken);
            return Results.Created($"/api/security/invitations/{created.InvitationId}", created);
        }
        catch (InvitationInputException exception) { return UserManagementEndpoints.Problem(exception.Code, exception.Message, 400); }
        catch (InvitationConflictException exception) { return UserManagementEndpoints.Problem(exception.Code, exception.Message, 409); }
    }

    private static async Task<IResult> ResendAsync(Guid invitationId, InvitationService service, HttpContext http,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.ResendAsync(invitationId, UserManagementEndpoints.UserId(http.User),
                UserManagementEndpoints.RequestContext(http), cancellationToken));
        }
        catch (InvitationNotFoundException) { return UserManagementEndpoints.Problem("invitation_not_found", "The invitation was not found.", 404); }
    }

    private static async Task<IResult> RevokeAsync(Guid invitationId, InvitationService service, HttpContext http,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.RevokeAsync(invitationId, UserManagementEndpoints.UserId(http.User), cancellationToken);
            return Results.NoContent();
        }
        catch (InvitationNotFoundException) { return UserManagementEndpoints.Problem("invitation_not_found", "The invitation was not found.", 404); }
    }

    private static async Task<IResult> InspectAsync(InvitationTokenRequest request, InvitationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.InspectAsync(request.Token, cancellationToken);
        return result is null ? UserManagementEndpoints.Problem("invitation_invalid",
            "This invitation is invalid or no longer available.", 400) : Results.Ok(result);
    }

    private static async Task<IResult> AcceptAsync(AcceptInvitationRequest request, InvitationService service,
        HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var accepted = await service.AcceptNewAsync(request.Token, request.Password,
                UserManagementEndpoints.RequestContext(http), cancellationToken);
            await http.SignInAsync(SignInService.PrincipalFor(accepted.User, accepted.Choice));
            return Results.Ok(SessionView.From(accepted.User, accepted.Choice, accepted.Tenants));
        }
        catch (InvitationInvalidException) { return Invalid(); }
        catch (InvitationAccountExistsException) { return UserManagementEndpoints.Problem("invitation_account_exists", "Sign in to accept this invitation.", 409); }
        catch (InvitationPasswordException exception)
        {
            return Results.Json(new { title = "password_policy", reasons = exception.Reasons }, statusCode: 400);
        }
    }

    private static async Task<IResult> AcceptSignedInAsync(InvitationTokenRequest request, InvitationService service,
        HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var accepted = await service.AcceptExistingAsync(request.Token, UserManagementEndpoints.UserId(http.User),
                http.User.FindFirstValue(YojanaClaim.TenantCode), UserManagementEndpoints.RequestContext(http),
                cancellationToken);
            await http.SignInAsync(SignInService.PrincipalFor(accepted.User, accepted.Choice));
            return Results.Ok(SessionView.From(accepted.User, accepted.Choice, accepted.Tenants));
        }
        catch (InvitationInvalidException) { return Invalid(); }
        catch (InvitationOtherAccountException) { return UserManagementEndpoints.Problem("invitation_other_account", "Sign in with the invited e-mail address.", 403); }
    }

    private static IResult Invalid() => UserManagementEndpoints.Problem("invitation_invalid",
        "This invitation is invalid or no longer available.", 400);
}

public sealed record CreateInvitationRequest(string Email, string DisplayName, IReadOnlyList<string> Roles);
public sealed record InvitationTokenRequest(string Token);
public sealed record AcceptInvitationRequest(string Token, string Password);
