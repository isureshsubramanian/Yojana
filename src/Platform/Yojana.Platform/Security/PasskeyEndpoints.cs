using System.Security.Claims;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public static class PasskeyEndpoints
{
    public const string PasswordlessMode = "passwordless";
    public const string SecondStepMode = "second_step";

    public static IEndpointRouteBuilder MapYojanaPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var signIn = endpoints.MapGroup("/api/auth/passkey/sign-in").AllowAnonymous()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        signIn.MapPost("/start", StartSignInAsync);
        signIn.MapPost("/finish", FinishSignInAsync);

        var account = endpoints.MapGroup("/api/auth/passkeys").RequireAuthorization();
        account.MapGet("", AccountAsync);
        account.MapPost("/registration/start", StartRegistrationAsync).RequireRateLimiting(SecurityRateLimiting.PolicyName);
        account.MapPost("/registration/finish", FinishRegistrationAsync);
        account.MapPost("/{passkeyId:guid}/remove", RemoveAsync).RequireRateLimiting(SecurityRateLimiting.PolicyName);

        var settings = endpoints.MapGroup("/api/security/settings/passkeys")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        settings.MapGet("", GetSettingsAsync);
        settings.MapPut("", PutSettingsAsync);
        return endpoints;
    }

    /// <summary>
    /// Passwordless: anyone may ask, the browser offers the site's passkeys. Second step: only after a correct password
    /// (the pending second-step cookie), and only that person's passkeys are offered.
    /// </summary>
    private static async Task<IResult> StartSignInAsync(PasskeySignInStartRequest request,
        PendingSecondStepCookie pendingCookie, PasskeyService passkeys, HttpContext http,
        CancellationToken cancellationToken)
    {
        Guid? userId = null;
        if (request.Mode == SecondStepMode)
        {
            var pending = await pendingCookie.ReadAsync(http, cancellationToken);
            if (pending is null || pending.EnrolmentRequired) return Expired();
            userId = pending.UserId;
        }
        else if (request.Mode != PasswordlessMode) return Problem("passkey_mode_invalid", "Choose a sign-in mode.", 400);
        return await Guard(async () => Results.Ok(await passkeys.StartSignInAsync(userId, cancellationToken)));
    }

    private static async Task<IResult> FinishSignInAsync(PasskeySignInFinishRequest request,
        PendingSecondStepCookie pendingCookie, PasskeyService passkeys, SignInService signIn, HttpContext http,
        CancellationToken cancellationToken)
    {
        Guid? userId = null;
        if (request.Mode == SecondStepMode)
        {
            var pending = await pendingCookie.ReadAsync(http, cancellationToken);
            if (pending is null || pending.EnrolmentRequired) return Expired();
            userId = pending.UserId;
        }
        else if (request.Mode != PasswordlessMode) return Problem("passkey_mode_invalid", "Choose a sign-in mode.", 400);
        return await Guard(async () =>
        {
            var result = await passkeys.FinishSignInAsync(userId, request.CeremonyId, request.Credential,
                RequestContext(http), cancellationToken);
            if (result.Locked) return Results.Unauthorized();
            if (result.NotAllowed)
                return Problem("passkey_not_allowed", "Your company does not allow signing in with a passkey alone.", 403);
            var user = result.User!;
            var tenants = await signIn.TenantsForAsync(user.Id, cancellationToken);
            var selected = tenants.Count == 1 ? tenants[0] : null;
            await signIn.RecordSignInSucceededAsync(user, selected?.TenantId, RequestContext(http), cancellationToken,
                userId is null ? "passkey" : "password+passkey");
            await http.SignInAsync(SignInService.PrincipalFor(user, selected));
            pendingCookie.Clear(http);
            return Results.Ok(SessionView.From(user, selected, tenants));
        });
    }

    private static async Task<IResult> AccountAsync(PasskeyService passkeys, HttpContext http,
        CancellationToken cancellationToken) => Results.Ok(await passkeys.AccountAsync(UserId(http.User), cancellationToken));

    private static async Task<IResult> StartRegistrationAsync(PasskeyRegistrationStartRequest request,
        PasskeyService passkeys, HttpContext http, CancellationToken cancellationToken) =>
        await Guard(async () => Results.Ok(await passkeys.StartRegistrationAsync(UserId(http.User), request.Name,
            request.CurrentPassword, cancellationToken)));

    private static async Task<IResult> FinishRegistrationAsync(PasskeyRegistrationFinishRequest request,
        PasskeyService passkeys, HttpContext http, CancellationToken cancellationToken) =>
        await Guard(async () => Results.Ok(await passkeys.FinishRegistrationAsync(UserId(http.User), request.CeremonyId,
            request.Credential, SelectedTenantId(http.User), RequestContext(http), cancellationToken)));

    private static async Task<IResult> RemoveAsync(Guid passkeyId, CurrentPasswordRequest request,
        PasskeyService passkeys, SignInService signIn, HttpContext http, CancellationToken cancellationToken) =>
        await Guard(async () =>
        {
            var user = await passkeys.RemoveAsync(UserId(http.User), passkeyId, request.CurrentPassword,
                SelectedTenantId(http.User), RequestContext(http), cancellationToken);
            // The security stamp rotated: re-issue this session so only the other sessions end.
            var tenants = await signIn.TenantsForAsync(user.Id, cancellationToken);
            var selectedCode = http.User.FindFirstValue(YojanaClaim.TenantCode);
            await http.SignInAsync(SignInService.PrincipalFor(user, tenants.FirstOrDefault(row => row.Code == selectedCode)));
            return Results.NoContent();
        });

    private static async Task<IResult> GetSettingsAsync(PasskeyPolicy policy, CancellationToken cancellationToken) =>
        Results.Ok(await policy.GetCurrentAsync(cancellationToken));

    private static async Task<IResult> PutSettingsAsync(PasskeySettingsRequest request, PasskeyPolicy policy,
        SecurityEventWriter events, PlatformDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var (view, changed) = await policy.UpdateCurrentAsync(request.Allowed, request.Passwordless,
                request.Version ?? string.Empty, cancellationToken);
            foreach (var key in changed)
                events.Add(SecurityEventKind.SettingsChanged, null, SelectedTenantId(http.User), UserId(http.User),
                    RequestContext(http), key);
            if (changed.Count > 0) await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(view);
        }
        catch (ArgumentException error) { return Problem("passkey_settings_invalid", error.Message, 400); }
        catch (TenantSettingVersionConflictException)
        {
            return Problem("passkey_settings_conflict",
                "The passkey settings changed after they were loaded. Reload and try again.", 409);
        }
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (PasskeyUnavailableException)
        {
            return Problem("passkey_unavailable", "Passkeys are not set up on this server.", 503);
        }
        catch (PasskeyNotAllowedException)
        {
            return Problem("passkey_not_allowed", "Your company does not allow passkeys.", 403);
        }
        catch (PasskeyCeremonyExpiredException)
        {
            return Problem("passkey_ceremony_expired", "That request has expired. Try again.", 410);
        }
        catch (PasskeyRejectedException)
        {
            return Problem("passkey_rejected", "The passkey was not accepted.", 400);
        }
        catch (PasskeyNameInvalidException)
        {
            return Problem("passkey_name_invalid",
                $"Give the passkey a printable name of at most {PasskeyService.MaximumNameLength} characters.", 400);
        }
        catch (PasskeyLimitReachedException)
        {
            return Problem("passkey_limit",
                $"You already have {PasskeyService.MaximumPerUser} passkeys. Remove one first.", 409);
        }
        catch (PasskeyNotFoundException) { return Problem("passkey_not_found", "That passkey was not found.", 404); }
        catch (SecondStepPasswordWrongException)
        {
            return Problem("current_password_wrong", "The current password was not accepted.", 400);
        }
        catch (SecondStepRemovalNotAllowedException)
        {
            return Problem("second_step_required",
                "Your company requires a second step, and this is your last one.", 403);
        }
    }

    private static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(YojanaClaim.UserId)!);
    private static TenantId? SelectedTenantId(ClaimsPrincipal principal) =>
        TenantId.TryParse(principal.FindFirstValue(YojanaClaim.TenantId), out var tenantId) ? tenantId : null;
    // TIGHT COUPLING (trusted client address), as in SecurityEndpoints: SignInThrottleService keys addresses on
    // RemoteIpAddress, which the configured forwarded-header middleware must already have set.
    private static SecurityRequestContext RequestContext(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
    private static IResult Expired() => Problem("second_step_expired", "Sign in with your password again.", 401);
    private static IResult Problem(string code, string detail, int status) =>
        Results.Problem(title: code, detail: detail, statusCode: status);
}

public sealed record PasskeySignInStartRequest(string? Mode);
public sealed record PasskeySignInFinishRequest(Guid CeremonyId, string? Mode,
    AuthenticatorAssertionRawResponse? Credential);
public sealed record PasskeyRegistrationStartRequest(string? Name, string? CurrentPassword);
public sealed record PasskeyRegistrationFinishRequest(Guid CeremonyId, AuthenticatorAttestationRawResponse? Credential);
public sealed record PasskeySettingsRequest(bool Allowed, bool Passwordless, string? Version);
