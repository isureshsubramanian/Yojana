using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Clients;

namespace Yojana.Developer.Authentication;

public static class PublicApiRateLimiting
{
    public const string ClientPolicy = "public_api_client";
    public static IServiceCollection AddPublicApiRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(ClientPolicy, http =>
            {
                var clientId = http.User.FindFirst(PublicApiAuthentication.ClientIdClaim)?.Value ?? "anonymous";
                var value = http.User.FindFirst(PublicApiAuthentication.RateLimitClaim)?.Value;
                var limit = int.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
                    ? Math.Clamp(parsed, 1, ApiClientService.MaximumRateLimitPerMinute)
                    // Authentication owns anonymous rejection. A one-request shared anonymous bucket would turn
                    // later invalid credentials into 429 and obscure the stable authentication refusal code.
                    : ApiClientService.MaximumRateLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(clientId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
}
