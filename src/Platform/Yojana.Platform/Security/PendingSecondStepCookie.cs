using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public sealed record PendingSecondStep(Guid UserId, Guid SecurityStamp, DateTime ExpiresAt,
    bool EnrolmentRequired);

public sealed class PendingSecondStepCookie(
    IDataProtectionProvider protection,
    PlatformDbContext db,
    IClock clock)
{
    public const string Name = "yojana.pending-second-step";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector protector = protection.CreateProtector("Yojana.PendingSecondStep.v1");

    public void Issue(HttpContext http, Guid userId, Guid securityStamp, bool enrolmentRequired)
    {
        var value = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new PendingSecondStep(userId,
            securityStamp, clock.UtcNow + Lifetime, enrolmentRequired)));
        http.Response.Cookies.Append(Name, Convert.ToBase64String(value), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/api/auth",
            MaxAge = Lifetime,
            IsEssential = true,
        });
    }

    public async Task<PendingSecondStep?> ReadAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (!http.Request.Cookies.TryGetValue(Name, out var encoded)) return null;
        try
        {
            var payload = protector.Unprotect(Convert.FromBase64String(encoded));
            var pending = JsonSerializer.Deserialize<PendingSecondStep>(payload);
            if (pending is null || pending.ExpiresAt <= clock.UtcNow) return null;
            var current = await db.Users.AsNoTracking().Where(row => row.Id == pending.UserId && row.IsActive)
                .Select(row => (Guid?)row.SecurityStamp).SingleOrDefaultAsync(cancellationToken);
            return current == pending.SecurityStamp ? pending : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    public void Clear(HttpContext http) => http.Response.Cookies.Delete(Name,
        new CookieOptions { Path = "/api/auth", SameSite = SameSiteMode.Strict });
}
