using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public static class PasswordResetEndpoints
{
    public static IEndpointRouteBuilder MapYojanaPasswordResetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapPost("/password-reset/request", RequestAsync).AllowAnonymous()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/password-reset/complete", CompleteAsync).AllowAnonymous()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/change-password", ChangeAsync).RequireAuthorization()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);

        var security = endpoints.MapGroup("/api/security")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        security.MapGet("/users/{userId:guid}/password-reset-options", OptionsAsync);
        security.MapPost("/users/{userId:guid}/password-reset", AdminResetAsync);
        security.MapGet("/settings/passwords", GetSettingsAsync);
        security.MapPut("/settings/passwords", PutSettingsAsync);
        return endpoints;
    }

    private static async Task<IResult> RequestAsync(PasswordResetRequest request, PasswordResetService resets,
        HttpContext http, CancellationToken cancellationToken)
    {
        await resets.RequestAsync(request.Email, RequestContext(http), cancellationToken);
        return Results.Json(new { }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> CompleteAsync(PasswordResetCompleteRequest request,
        PasswordResetService resets, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await resets.CompleteAsync(request.Token, request.NewPassword ?? string.Empty,
            RequestContext(http), cancellationToken);
        if (result.Policy is { IsValid: false } policy) return PolicyProblem(policy);
        if (result.Completion is null)
            return Results.Problem(title: "reset_link_invalid",
                detail: "This password-reset link is invalid or has expired.",
                statusCode: StatusCodes.Status400BadRequest);

        var selected = result.Completion.Tenants.Count == 1 ? result.Completion.Tenants[0] : null;
        await http.SignInAsync(SignInService.PrincipalFor(result.Completion.User, selected));
        return Results.Ok(SessionView.From(result.Completion.User, selected, result.Completion.Tenants));
    }

    private static async Task<IResult> ChangeAsync(ChangePasswordRequest request, PasswordResetService resets,
        SignInService signIn, HttpContext http, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.TryParse(http.User.FindFirstValue(YojanaClaim.TenantId), out var selectedTenant)
            ? selectedTenant : (TenantId?)null;
        var result = await resets.ChangeAsync(UserId(http.User), request.CurrentPassword ?? string.Empty,
            request.NewPassword ?? string.Empty, tenantId, http.User.FindFirstValue(YojanaClaim.TenantCode) is null
                ? null : (await signIn.TenantsForAsync(UserId(http.User), cancellationToken))
                    .FirstOrDefault(row => row.Code == http.User.FindFirstValue(YojanaClaim.TenantCode))?.Name,
            RequestContext(http), cancellationToken);
        if (result.CurrentPasswordWrong)
            return Results.Problem(title: "current_password_wrong", detail: "The current password is not correct.",
                statusCode: StatusCodes.Status400BadRequest);
        if (result.Policy is { IsValid: false } policy) return PolicyProblem(policy);

        var tenants = await signIn.TenantsForAsync(result.User!.Id, cancellationToken);
        var selectedCode = http.User.FindFirstValue(YojanaClaim.TenantCode);
        var selected = selectedCode is null ? null : tenants.FirstOrDefault(row => row.Code == selectedCode);
        await http.SignInAsync(SignInService.PrincipalFor(result.User, selected));
        return Results.NoContent();
    }

    private static async Task<IResult> OptionsAsync(Guid userId, PasswordResetService resets, HttpContext http,
        CancellationToken cancellationToken)
    {
        var actorId = UserId(http.User);
        if (actorId == userId)
            return Results.Problem(title: "use_change_password",
                detail: "Use change password for your own account.", statusCode: StatusCodes.Status403Forbidden);
        var result = await resets.AdminOptionsAsync(actorId, userId, SelectedTenantId(http.User), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> AdminResetAsync(Guid userId, AdminPasswordResetRequest request,
        PasswordResetService resets, HttpContext http, CancellationToken cancellationToken)
    {
        var actorId = UserId(http.User);
        if (actorId == userId)
            return Results.Problem(title: "use_change_password",
                detail: "Use change password for your own account.", statusCode: StatusCodes.Status403Forbidden);
        try
        {
            var result = await resets.AdminResetAsync(actorId, userId, SelectedTenantId(http.User),
                http.User.FindFirstValue(YojanaClaim.TenantCode) is null ? "Yojana"
                    : await TenantNameAsync(http, cancellationToken),
                request.Mode ?? string.Empty, RequestContext(http), cancellationToken);
            if (result is null) return Results.NotFound();
            return Results.Json(result, statusCode: result.Mode == "link"
                ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }
        catch (TemporaryPasswordNotAllowedException exception)
        {
            return Results.Problem(title: "temporary_password_not_allowed",
                detail: "A temporary password is not available for this user.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["reason"] = exception.Reason });
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(title: "password_reset_mode_invalid", detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<string> TenantNameAsync(HttpContext http,
        CancellationToken cancellationToken)
    {
        // The service already owns tenant membership reads. The selected tenant's display name is carried by the
        // session view contract, so resolve it through the same sign-in service at the endpoint boundary.
        var signIn = http.RequestServices.GetRequiredService<SignInService>();
        var code = http.User.FindFirstValue(YojanaClaim.TenantCode);
        return (await signIn.TenantsForAsync(UserId(http.User), cancellationToken))
            .FirstOrDefault(row => row.Code == code)?.Name ?? "Yojana";
    }

    private static async Task<IResult> GetSettingsAsync(PasswordSecuritySettings settings,
        CancellationToken cancellationToken) => Results.Ok(await settings.GetCurrentAsync(cancellationToken));

    private static async Task<IResult> PutSettingsAsync(PasswordSecuritySettingsRequest request,
        PasswordSecuritySettings settings, SecurityEventWriter events, PlatformDbContext db, HttpContext http,
        CancellationToken cancellationToken)
    {
        try
        {
            var (view, changed) = await settings.UpdateCurrentAsync(request.ResetLinkMinutes,
                request.AdminTemporaryPasswordAllowed, request.InvitationValidDays, request.Version ?? string.Empty,
                cancellationToken);
            if (changed.Count > 0)
            {
                events.Add(SecurityEventKind.SettingsChanged, null, SelectedTenantId(http.User), UserId(http.User),
                    RequestContext(http), string.Join(',', changed));
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(view);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Results.Problem(title: "password_settings_invalid", detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TenantSettingVersionConflictException)
        {
            return Results.Problem(title: "password_settings_conflict",
                detail: "The sign-in policy changed after it was loaded. Reload and try again.",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult PolicyProblem(PasswordPolicyResult policy) =>
        Results.Problem(title: "password_policy_failed", detail: "Choose a password that meets every rule.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["reasons"] = policy.Reasons });

    private static Guid UserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(YojanaClaim.UserId)!);

    private static TenantId SelectedTenantId(ClaimsPrincipal user) =>
        new(Guid.Parse(user.FindFirstValue(YojanaClaim.TenantId)!));

    private static SecurityRequestContext RequestContext(HttpContext http) => new(
        // TIGHT COUPLING (trusted client address): reset throttling and audit use the same RemoteIpAddress boundary as
        // sign-in. PlatformSetup must keep its allow-listed forwarded-header middleware ahead of this endpoint.
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}

public sealed record PasswordResetRequest(string? Email);
public sealed record PasswordResetCompleteRequest(string? Token, string? NewPassword);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record AdminPasswordResetRequest(string? Mode);
public sealed record PasswordSecuritySettingsRequest(int ResetLinkMinutes, bool AdminTemporaryPasswordAllowed,
    int InvitationValidDays, string? Version);
