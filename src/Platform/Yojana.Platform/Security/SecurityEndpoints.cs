using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Security;

public static class SecurityEndpoints
{
    internal static readonly SortedPage<SecurityEvent, Guid> EventPages = new("when", row => row.Id,
        new SortColumn<SecurityEvent, DateTime>("when", row => row.OccurredAt, SortDirection.Descending),
        new SortColumn<SecurityEvent, string>("event", row => row.Kind));

    public static IEndpointRouteBuilder MapYojanaSecurityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapPost("/sign-in", SignInAsync).AllowAnonymous()
            .RequireRateLimiting(SecurityRateLimiting.PolicyName);
        auth.MapPost("/select-tenant", SelectTenantAsync).RequireAuthorization();
        auth.MapGet("/me", MeAsync).RequireAuthorization();
        auth.MapPost("/sign-out", SignOutAsync).AllowAnonymous();

        endpoints.MapGet("/api/security/events", SecurityEventsAsync)
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        endpoints.MapGet("/api/security/events/summary", SecurityEventSummaryAsync)
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        return endpoints;
    }

    private static async Task<IResult> SignInAsync(SignInRequest request, SignInService signIn,
        SecondStepService secondStep, PendingSecondStepCookie pendingCookie, HttpContext http,
        CancellationToken cancellationToken)
    {
        var context = RequestContext(http);
        // A correct password is only the first half of an enrolled sign-in. Keep its account-failure history until
        // the second step succeeds, otherwise repeating the password would erase every bad authenticator attempt.
        var attempt = await signIn.AuthenticateAsync(request.Email, request.Password, context, cancellationToken,
            completeSuccess: false);
        if (attempt.User is null)
        {
            if (attempt.AddressRetryAfter is { } retryAfter)
            {
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(CultureInfo.InvariantCulture);
            }
            return Results.Unauthorized();
        }

        var challenge = await secondStep.ChallengeAsync(attempt.User.Id, cancellationToken);
        var tenants = await signIn.TenantsForAsync(attempt.User.Id, cancellationToken);
        if (challenge.Required)
        {
            pendingCookie.Issue(http, attempt.User.Id, attempt.User.SecurityStamp, challenge.EnrolmentRequired);
            return Results.Ok(new PendingSecondStepView(true, challenge.Methods, challenge.EnrolmentRequired,
                attempt.User.DisplayName, tenants.Select(tenant => new TenantChoiceView(tenant.Code, tenant.Name,
                    tenant.Roles)).ToList()));
        }
        var completion = await signIn.CompleteSuccessAsync(attempt.User, context, cancellationToken);
        if (completion.AccountLocked || completion.AddressRetryAfter is not null) return Results.Unauthorized();
        var selected = tenants.Count == 1 ? tenants[0] : null;
        await signIn.RecordSignInSucceededAsync(attempt.User, selected?.TenantId, context, cancellationToken);
        await http.SignInAsync(SignInService.PrincipalFor(attempt.User, selected));
        return Results.Ok(SessionView.From(attempt.User, selected, tenants));
    }

    private static async Task<IResult> SelectTenantAsync(SelectTenantRequest request, SignInService signIn,
        PlatformDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        var userId = UserId(http.User);
        var tenants = await signIn.TenantsForAsync(userId, cancellationToken);
        var choice = tenants.FirstOrDefault(tenant => tenant.Code == request.TenantCode);
        if (choice is null) return Results.Forbid();
        var user = await db.Users.AsNoTracking().FirstAsync(row => row.Id == userId, cancellationToken);
        await http.SignInAsync(SignInService.PrincipalFor(user, choice));
        return Results.Ok(SessionView.From(user, choice, tenants));
    }

    private static async Task<IResult> MeAsync(SignInService signIn, PlatformDbContext db, HttpContext http,
        CancellationToken cancellationToken)
    {
        var userId = UserId(http.User);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == userId && row.IsActive, cancellationToken);
        if (user is null)
        {
            await http.SignOutAsync();
            return Results.Unauthorized();
        }

        var tenants = await signIn.TenantsForAsync(userId, cancellationToken);
        var code = http.User.FindFirstValue(YojanaClaim.TenantCode);
        var selected = code is null ? null : tenants.FirstOrDefault(tenant => tenant.Code == code);
        return Results.Ok(SessionView.From(user, selected, tenants));
    }

    private static async Task<IResult> SignOutAsync(SignInService signIn, PendingSecondStepCookie pendingCookie,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        pendingCookie.Clear(http);
        if (http.User.Identity?.IsAuthenticated != true) return Results.NoContent();
        var tenantId = TenantId.TryParse(http.User.FindFirstValue(YojanaClaim.TenantId), out var selected)
            ? selected : (TenantId?)null;
        await signIn.RecordSignOutAsync(UserId(http.User), tenantId, RequestContext(http), cancellationToken);
        await http.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> SecurityEventsAsync(string? kind, Guid? userId, DateTime? from,
        DateTime? to, string? sort, string? dir, string? cursor, int? take, SecurityEventQueries queries,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await queries.PageAsync(kind, userId, from, to, sort, dir, cursor, take,
                cancellationToken));
        }
        catch (CommandRefusedException exception)
        {
            return Results.Problem(title: exception.Rule, detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["rule"] = exception.Rule });
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(title: "security_event_query_invalid", detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> SecurityEventSummaryAsync(DateTime? todayFrom, DateTime? weekFrom,
        SecurityEventQueries queries, CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await queries.SummaryAsync(todayFrom, weekFrom, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(title: "security_event_summary_invalid", detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static Guid UserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(YojanaClaim.UserId)!);

    private static SecurityRequestContext RequestContext(HttpContext http) => new(
        // TIGHT COUPLING (trusted client address): SignInThrottleService uses this RemoteIpAddress as its address key.
        // PlatformSetup must run the explicitly configured forwarded-header middleware first; reading the header here
        // would let a client choose its throttle key, while omitting that middleware would group users by proxy address.
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}

public sealed record SignInRequest(string Email, string Password);
public sealed record SelectTenantRequest(string TenantCode);

public sealed record SessionView(SessionUserView User, SessionTenantView? Tenant, IReadOnlyList<string> Roles,
    IReadOnlyList<TenantChoiceView> Tenants, bool PasswordChangeRequired)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RecoveryCodesLow { get; init; }

    public static SessionView From(AppUser user, TenantChoice? selected, IReadOnlyList<TenantChoice> tenants,
        bool recoveryCodesLow = false) => new(
        new SessionUserView(user.Id, user.Email, user.DisplayName),
        selected is null ? null : new SessionTenantView(selected.Code, selected.Name),
        selected?.Roles.Where(YojanaRole.IsKnown).ToList() ?? [],
        tenants.Select(tenant => new TenantChoiceView(tenant.Code, tenant.Name, tenant.Roles)).ToList(),
        user.MustChangePassword) { RecoveryCodesLow = recoveryCodesLow };
}

public sealed record SessionUserView(Guid Id, string Email, string DisplayName);
public sealed record SessionTenantView(string Code, string Name);
public sealed record TenantChoiceView(string Code, string Name, IReadOnlyList<string> Roles);
public sealed record PendingSecondStepView(bool SecondStepRequired, IReadOnlyList<string> Methods,
    bool EnrolmentRequired, string UserName, IReadOnlyList<TenantChoiceView> TenantSummary);
