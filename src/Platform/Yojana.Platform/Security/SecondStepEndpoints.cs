using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public static class SecondStepEndpoints
{
    public static IEndpointRouteBuilder MapYojanaSecondStepEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth/second-step");
        auth.MapPost("", CompleteAsync).AllowAnonymous().RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapGet("", AccountAsync).RequireAuthorization();
        auth.MapPost("/enrolment/start", StartAsync).AllowAnonymous()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/enrolment/confirm", ConfirmAsync).AllowAnonymous()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/recovery-codes", RecoveryCodesAsync).RequireAuthorization();
        auth.MapPost("/remove", RemoveAsync).RequireAuthorization().RequireRateLimiting(SecurityRateLimiting.PolicyName);

        var settings = endpoints.MapGroup("/api/security/settings/second-step")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        settings.MapGet("", GetPolicyAsync);
        settings.MapPut("", PutPolicyAsync);

        endpoints.MapPost("/api/security/users/{userId:guid}/second-step-reset", AdminResetAsync)
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        return endpoints;
    }

    private static async Task<IResult> CompleteAsync(SecondStepRequest request, PendingSecondStepCookie pendingCookie,
        SecondStepService service, SignInService signIn, HttpContext http, CancellationToken cancellationToken)
    {
        var pending = await pendingCookie.ReadAsync(http, cancellationToken);
        if (pending is null || pending.EnrolmentRequired) return Expired();
        try
        {
            var verified = await service.VerifyAsync(pending.UserId, request.Method, request.Code,
                RequestContext(http), cancellationToken);
            if (verified.Locked) return Results.Unauthorized();
            if (verified.Wrong) return Problem("second_step_code_wrong", "The code was not accepted.", 400);
            var tenants = await signIn.TenantsForAsync(pending.UserId, cancellationToken);
            var selected = tenants.Count == 1 ? tenants[0] : null;
            await signIn.RecordSignInSucceededAsync(verified.User!, selected?.TenantId, RequestContext(http),
                cancellationToken, "password+" + request.Method);
            await http.SignInAsync(SignInService.PrincipalFor(verified.User!, selected));
            pendingCookie.Clear(http);
            return Results.Ok(SessionView.From(verified.User!, selected, tenants, verified.RecoveryCodesLow));
        }
        catch (SecondStepUnavailableException)
        {
            return Problem("second_step_unavailable", "The second-step service is unavailable.", 503);
        }
    }

    private static async Task<IResult> StartAsync(PendingSecondStepCookie pendingCookie,
        SecondStepService service, HttpContext http, CancellationToken cancellationToken)
    {
        Guid? userId = AuthenticatedUserId(http.User);
        if (userId is null)
        {
            var pending = await pendingCookie.ReadAsync(http, cancellationToken);
            if (pending is null || !pending.EnrolmentRequired) return Expired();
            userId = pending.UserId;
        }
        try { return Results.Ok(await service.StartAsync(userId.Value, cancellationToken)); }
        catch (SecondStepUnavailableException)
        {
            return Problem("second_step_unavailable", "The second-step service is unavailable.", 503);
        }
    }

    private static async Task<IResult> ConfirmAsync(SecondStepConfirmRequest request,
        PendingSecondStepCookie pendingCookie, SecondStepService service, SignInService signIn,
        HttpContext http, CancellationToken cancellationToken)
    {
        var signedIn = AuthenticatedUserId(http.User);
        var pending = signedIn is null ? await pendingCookie.ReadAsync(http, cancellationToken) : null;
        if (signedIn is null && (pending is null || !pending.EnrolmentRequired)) return Expired();
        var userId = signedIn ?? pending!.UserId;
        try
        {
            var tenantsBeforeConfirmation = pending is null ? null : await signIn.TenantsForAsync(userId,
                cancellationToken);
            var automaticTenant = tenantsBeforeConfirmation?.Count == 1 ? tenantsBeforeConfirmation[0] : null;
            var result = await service.ConfirmAsync(userId, request.EnrolmentId, request.Code,
                request.CurrentPassword, pending is null, SelectedTenantId(http.User) ?? automaticTenant?.TenantId, RequestContext(http), cancellationToken);
            SessionView? session = null;
            if (pending is not null)
            {
                var tenants = tenantsBeforeConfirmation!;
                var selected = tenants.Count == 1 ? tenants[0] : null;
                var completion = await signIn.CompleteSuccessAsync(result.User, RequestContext(http),
                    cancellationToken);
                if (completion.AccountLocked || completion.AddressRetryAfter is not null)
                    return Results.Unauthorized();
                await signIn.RecordSignInSucceededAsync(result.User, selected?.TenantId, RequestContext(http),
                    cancellationToken, "password+authenticator");
                await http.SignInAsync(SignInService.PrincipalFor(result.User, selected));
                pendingCookie.Clear(http);
                session = SessionView.From(result.User, selected, tenants);
            }
            else if (result.Replaced)
            {
                var tenants = await signIn.TenantsForAsync(userId, cancellationToken);
                var selectedCode = http.User.FindFirstValue(YojanaClaim.TenantCode);
                var selected = tenants.FirstOrDefault(row => row.Code == selectedCode);
                await http.SignInAsync(SignInService.PrincipalFor(result.User, selected));
                session = SessionView.From(result.User, selected, tenants);
            }
            return Results.Ok(new SecondStepConfirmView(result.RecoveryCodes, session));
        }
        catch (SecondStepUnavailableException)
        {
            return Problem("second_step_unavailable", "The second-step service is unavailable.", 503);
        }
        catch (SecondStepCodeWrongException)
        {
            if (pending is not null && await service.RegisterWrongSignInCodeAsync(userId, RequestContext(http),
                    cancellationToken))
                return Results.Unauthorized();
            return Problem("second_step_code_wrong", "The code was not accepted.", 400);
        }
        catch (SecondStepEnrolmentNotFoundException)
        {
            return Problem("second_step_enrolment_expired", "Start authenticator enrolment again.", 410);
        }
        catch (SecondStepPasswordWrongException)
        {
            return Problem("current_password_wrong", "The current password was not accepted.", 400);
        }
    }

    private static async Task<IResult> AccountAsync(SecondStepService service, HttpContext http,
        CancellationToken cancellationToken) => Results.Ok(await service.AccountAsync(UserId(http.User),
            cancellationToken));

    private static async Task<IResult> RecoveryCodesAsync(AuthenticatorCodeRequest request,
        SecondStepService service, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(new RecoveryCodesView(await service.RegenerateRecoveryCodesAsync(UserId(http.User),
                request.Code, SelectedTenantId(http.User), RequestContext(http), cancellationToken)));
        }
        catch (SecondStepCodeWrongException)
        {
            return Problem("second_step_code_wrong", "The authenticator code was not accepted.", 400);
        }
        catch (SecondStepUnavailableException)
        {
            return Problem("second_step_unavailable", "The second-step service is unavailable.", 503);
        }
    }

    private static async Task<IResult> RemoveAsync(CurrentPasswordRequest request, SecondStepService service,
        SignInService signIn, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var user = await service.RemoveAsync(UserId(http.User), request.CurrentPassword,
                SelectedTenantId(http.User), RequestContext(http), cancellationToken);
            var tenants = await signIn.TenantsForAsync(user.Id, cancellationToken);
            var selectedCode = http.User.FindFirstValue(YojanaClaim.TenantCode);
            var selected = tenants.FirstOrDefault(row => row.Code == selectedCode);
            await http.SignInAsync(SignInService.PrincipalFor(user, selected));
            return Results.NoContent();
        }
        catch (SecondStepRemovalNotAllowedException)
        {
            return Problem("second_step_required", "Your company requires a second step.", 403);
        }
        catch (SecondStepPasswordWrongException)
        {
            return Problem("current_password_wrong", "The current password was not accepted.", 400);
        }
    }

    private static async Task<IResult> GetPolicyAsync(SecondStepPolicy policy,
        CancellationToken cancellationToken) => Results.Ok(await policy.GetCurrentAsync(cancellationToken));

    private static async Task<IResult> PutPolicyAsync(SecondStepPolicyRequest request, SecondStepPolicy policy,
        SecurityEventWriter events, PlatformDbContext db, HttpContext http,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = request.Roles.Select(row => new SecondStepRoleSetting(row.Role, string.Empty, 0, row.Mode))
                .ToList();
            var (view, changed) = await policy.UpdateCurrentAsync(rows, request.Version, cancellationToken);
            if (changed.Count > 0)
            {
                // Audit reasons are capped at 100 characters. One event per role preserves every changed role name;
                // joining and truncating a large policy edit would silently lose part of the required evidence.
                foreach (var role in changed)
                    events.Add(SecurityEventKind.SettingsChanged, null, SelectedTenantId(http.User),
                        UserId(http.User), RequestContext(http), "second_step:" + role);
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(view);
        }
        catch (ArgumentException error) { return Problem("second_step_settings_invalid", error.Message, 400); }
        catch (TenantSettingVersionConflictException)
        {
            return Problem("second_step_settings_conflict",
                "The sign-in policy changed after it was loaded. Reload and try again.", 409);
        }
    }

    private static async Task<IResult> AdminResetAsync(Guid userId, MembershipReasonRequest request,
        SecondStepService service, MembershipAdministration administration, PlatformDbContext db,
        HttpContext http, CancellationToken cancellationToken)
    {
        var actorId = UserId(http.User);
        if (actorId == userId) return Problem("use_account_page", "Use your account security page.", 403);
        var tenantId = SelectedTenantId(http.User)!.Value;
        if (!await db.Memberships.AsNoTracking().AnyAsync(row => row.UserId == userId &&
            row.TenantId == tenantId, cancellationToken))
            return Problem("user_not_found", "The user is not a member of this tenant.", 404);
        if (!await administration.AdministersEveryMembershipAsync(actorId, userId, cancellationToken))
            return Problem("second_step_reset_not_allowed",
                "You must administer every membership of this user.", 403);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 100 ||
            request.Reason.Any(char.IsControl))
            return Problem("reason_invalid", "Give a printable reason of at most 100 characters.", 400);
        var actorName = await db.Users.AsNoTracking().Where(row => row.Id == actorId)
            .Select(row => row.DisplayName).SingleAsync(cancellationToken);
        await service.RemoveByAdministratorAsync(userId, actorId, tenantId, request.Reason.Trim(), actorName,
            RequestContext(http), cancellationToken);
        return Results.NoContent();
    }

    private static Guid UserId(ClaimsPrincipal principal) => AuthenticatedUserId(principal)!.Value;
    private static Guid? AuthenticatedUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(YojanaClaim.UserId), out var userId) ? userId : null;
    private static TenantId? SelectedTenantId(ClaimsPrincipal principal) =>
        TenantId.TryParse(principal.FindFirstValue(YojanaClaim.TenantId), out var tenantId) ? tenantId : null;
    private static SecurityRequestContext RequestContext(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
    private static IResult Expired() => Problem("second_step_expired",
        "Sign in with your password again.", 401);
    private static IResult Problem(string code, string detail, int status) =>
        Results.Problem(title: code, detail: detail, statusCode: status);
}

public sealed record SecondStepRequest(string? Method, string? Code);
public sealed record SecondStepConfirmRequest(Guid EnrolmentId, string? Code, string? CurrentPassword = null);
public sealed record AuthenticatorCodeRequest(string? Code);
public sealed record CurrentPasswordRequest(string? CurrentPassword);
public sealed record SecondStepConfirmView(IReadOnlyList<string> RecoveryCodes, SessionView? Session);
public sealed record RecoveryCodesView(IReadOnlyList<string> RecoveryCodes);
public sealed record SecondStepRoleRequest(string Role, string Mode);
public sealed record SecondStepPolicyRequest(IReadOnlyList<SecondStepRoleRequest> Roles, string Version);
