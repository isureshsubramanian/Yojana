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

public sealed record SignInAttemptResult(AppUser? User, TimeSpan? AddressRetryAfter = null);

/// <summary>Local sign-in and tenant selection. Runs before any tenant is set, against the global registry tables.</summary>
public sealed class SignInService(
    PlatformDbContext db,
    SignInThrottleService throttles,
    SecurityEventWriter events)
{
    // A real PBKDF2 hash with the production work factor. Unknown and locked identities verify against it so account
    // existence cannot be inferred by observing whether the expensive password operation ran.
    private const string DummyHash = "pbkdf2-sha256$600000$f1XI8DREsuAVIc+Ce5CqMQ==$+ueNypHBmhYZEu8VJWXA+8U+fAnGqjRquRGCJOjipzM=";

    public async Task<SignInAttemptResult> AuthenticateAsync(string? email, string? password,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        var clientAddress = string.IsNullOrWhiteSpace(request.ClientAddress) ? "unknown" : request.ClientAddress.Trim();
        var user = normalised.Length <= 320
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(row => row.Email == normalised, cancellationToken)
            : null;
        var lockState = await throttles.LockStateAsync(normalised, clientAddress, cancellationToken);
        if (lockState.AddressRetryAfter is { } addressRetry)
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash);
            events.Add(SecurityEventKind.LockedOut, user?.Id, null, null, request, "address_limit",
                user is null ? normalised : null);
            await db.SaveChangesAsync(cancellationToken);
            return new SignInAttemptResult(null, addressRetry);
        }

        if (lockState.AccountLocked)
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash);
            var addressFailure = await throttles.RegisterFailureAsync(normalised, clientAddress,
                includeAccount: false, cancellationToken);
            events.Add(SecurityEventKind.LockedOut, user?.Id, null, null, request, "account_lockout",
                user is null ? normalised : null);
            await db.SaveChangesAsync(cancellationToken);
            return new SignInAttemptResult(null, addressFailure.AddressRetryAfter);
        }

        var valid = false;
        if (user is { IsActive: true, PasswordHash: not null })
        {
            valid = PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash);
        }
        else
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash);
        }

        if (!valid)
        {
            var failed = await throttles.RegisterFailureAsync(normalised, clientAddress,
                includeAccount: true, cancellationToken);
            events.Add(SecurityEventKind.SignInFailed, user?.Id, null, null, request, "credentials_rejected",
                user is null ? normalised : null);
            if (failed.AccountLockedNow)
            {
                events.Add(SecurityEventKind.LockedOut, user?.Id, null, null, request, "account_lockout",
                    user is null ? normalised : null);
            }
            if (failed.AddressRetryAfter is not null)
            {
                events.Add(SecurityEventKind.LockedOut, user?.Id, null, null, request, "address_limit",
                    user is null ? normalised : null);
            }
            await db.SaveChangesAsync(cancellationToken);
            return new SignInAttemptResult(null, failed.AddressRetryAfter);
        }

        var completion = await throttles.CompleteSuccessAsync(normalised, clientAddress, cancellationToken);
        if (completion.AccountLocked || completion.AddressRetryAfter is not null)
        {
            events.Add(SecurityEventKind.LockedOut, user!.Id, null, null, request,
                completion.AddressRetryAfter is null ? "account_lockout" : "address_limit");
            await db.SaveChangesAsync(cancellationToken);
            return new SignInAttemptResult(null, completion.AddressRetryAfter);
        }
        return new SignInAttemptResult(user);
    }

    public async Task RecordSignInSucceededAsync(AppUser user, TenantId? tenantId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        events.Add(SecurityEventKind.SignInSucceeded, user.Id, tenantId, null, request, "password_accepted");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordSignOutAsync(Guid userId, TenantId? tenantId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        events.Add(SecurityEventKind.SignedOut, userId, tenantId, null, request, "user_requested");
        await db.SaveChangesAsync(cancellationToken);
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
            new(YojanaClaim.SecurityStamp, user.SecurityStamp.ToString("D")),
            new(YojanaClaim.SessionId, Guid.NewGuid().ToString("D")),
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

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name, YojanaClaim.Role));
    }
}
