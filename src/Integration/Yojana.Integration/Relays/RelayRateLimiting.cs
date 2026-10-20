using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Yojana.Integration.Relays;

public static class RelayRateLimiting
{
    public const string PolicyName = "relay";
    public static IServiceCollection AddRelayRateLimiting(this IServiceCollection services) => services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            http.Request.Path.StartsWithSegments("/api/relay/v1")
                ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("other"));
        options.AddPolicy(PolicyName, http => RateLimitPartition.GetFixedWindowLimiter(
            (http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Guid.TryParse(http.Request.Headers["X-Relay-Id"], out var id) ? id : Guid.Empty),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });
}
