using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public sealed record SecurityStampValidationResult(bool Accepted, ClaimsPrincipal Principal, bool RenewCookie);

public sealed class SecurityStampValidationCache(IClock clock, SignInOptions options)
{
    private readonly ConcurrentDictionary<string, CachedStamp> stamps = new(StringComparer.Ordinal);

    public bool IsCurrent(string sessionId, Guid stamp)
    {
        if (!stamps.TryGetValue(sessionId, out var cached)) return false;
        if (cached.ExpiresAt <= clock.UtcNow)
        {
            stamps.TryRemove(sessionId, out _);
            return false;
        }
        return cached.Stamp == stamp;
    }

    public void Remember(string sessionId, Guid stamp) =>
        stamps[sessionId] = new CachedStamp(stamp, clock.UtcNow + options.StampValidationInterval);

    private sealed record CachedStamp(Guid Stamp, DateTime ExpiresAt);
}

public sealed class SecurityStampValidator(
    PlatformDbContext db,
    SecurityStampValidationCache cache)
{
    public async Task<SecurityStampValidationResult> ValidateAsync(ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue(YojanaClaim.UserId), out var userId))
            return new SecurityStampValidationResult(false, principal, false);

        var sessionClaim = principal.FindFirstValue(YojanaClaim.SessionId);
        var stampClaim = principal.FindFirstValue(YojanaClaim.SecurityStamp);
        if (sessionClaim is not null && Guid.TryParse(stampClaim, out var claimedStamp) &&
            cache.IsCurrent(sessionClaim, claimedStamp))
        {
            return new SecurityStampValidationResult(true, principal, false);
        }

        var user = await db.Users.AsNoTracking().Where(row => row.Id == userId)
            .Select(row => new { row.IsActive, row.SecurityStamp }).FirstOrDefaultAsync(cancellationToken);
        if (user is null || !user.IsActive)
            return new SecurityStampValidationResult(false, principal, false);
        if (stampClaim is not null && (!Guid.TryParse(stampClaim, out claimedStamp) || claimedStamp != user.SecurityStamp))
            return new SecurityStampValidationResult(false, principal, false);

        var renew = stampClaim is null || sessionClaim is null;
        if (renew)
        {
            var identity = new ClaimsIdentity((ClaimsIdentity)principal.Identity!);
            foreach (var claim in identity.FindAll(YojanaClaim.SecurityStamp).ToList()) identity.RemoveClaim(claim);
            foreach (var claim in identity.FindAll(YojanaClaim.SessionId).ToList()) identity.RemoveClaim(claim);
            identity.AddClaim(new Claim(YojanaClaim.SecurityStamp, user.SecurityStamp.ToString("D")));
            sessionClaim = Guid.NewGuid().ToString("D");
            identity.AddClaim(new Claim(YojanaClaim.SessionId, sessionClaim));
            principal = new ClaimsPrincipal(identity);
        }

        cache.Remember(sessionClaim!, user.SecurityStamp);
        return new SecurityStampValidationResult(true, principal, renew);
    }
}

public static class SecurityStampCookieValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var validator = context.HttpContext.RequestServices.GetRequiredService<SecurityStampValidator>();
        var result = await validator.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted);
        if (!result.Accepted)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (result.RenewCookie)
        {
            context.ReplacePrincipal(result.Principal);
            context.ShouldRenew = true;
        }
    }
}
