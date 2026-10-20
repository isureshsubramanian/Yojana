using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Yojana.Platform.Security;

public static class SecurityRateLimiting
{
    public const string PolicyName = "authentication";

    public static IServiceCollection AddSecurityRateLimiting(this IServiceCollection services, SignInOptions settings) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(1,
                        (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(PolicyName, http => RateLimitPartition.GetFixedWindowLimiter(
                // TIGHT COUPLING (trusted client address): PlatformSetup's explicitly configured forwarded-header
                // middleware must run before this policy. Reading the header here would let an untrusted client choose
                // its partition; moving the middleware later would collapse proxied clients into the proxy's partition.
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.RequestLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
}
