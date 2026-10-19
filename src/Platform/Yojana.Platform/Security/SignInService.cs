using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Security;

public sealed record TenantChoice(TenantId TenantId, string Code, string Name, IReadOnlyList<string> Roles,
    Guid? CustomerPartyRef = null);

/// <summary>Local sign-in and tenant selection. Runs before any tenant is set, against the registry tables.</summary>
public sealed class SignInService(PlatformDbContext db)
{
    public async Task<AppUser?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalised = email.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == normalised && u.IsActive, cancellationToken);
        return user is not null && PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    public async Task<IReadOnlyList<TenantChoice>> TenantsForAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Tenant!.IsActive)
            .Select(m => new { m.TenantId, m.Tenant!.Code, m.Tenant.Name, m.Roles, m.CustomerPartyRef })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new TenantChoice(r.TenantId, r.Code, r.Name,
            r.Roles.Where(YojanaRole.IsKnown).ToList(), r.CustomerPartyRef))
            .OrderBy(r => r.Code).ToList();
    }

    public static ClaimsPrincipal PrincipalFor(AppUser user, TenantChoice? tenant)
    {
        var claims = new List<Claim>
        {
            new(YojanaClaim.UserId, user.Id.ToString("D")),
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
        };
        if (tenant is not null)
        {
            claims.Add(new Claim(YojanaClaim.TenantId, tenant.TenantId.ToString()));
            claims.Add(new Claim(YojanaClaim.TenantCode, tenant.Code));
            claims.AddRange(tenant.Roles.Where(YojanaRole.IsKnown).Select(r => new Claim(YojanaClaim.Role, r)));
            if (tenant.CustomerPartyRef is { } partyRef)
                claims.Add(new Claim(YojanaClaim.CustomerPartyRef, partyRef.ToString("D")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, YojanaClaim.Role));
    }
}
