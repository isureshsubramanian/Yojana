using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yojana.Developer.Clients;
using Yojana.Developer.Data;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Developer.Authentication;

public static class PublicApiAuthentication
{
    public const string Scheme = "YojanaApiClient";
    public const string Policy = "public_api_v1";
    public const string DocumentationPolicy = "public_api_v1_documentation";
    public const string ClientIdClaim = "yojana:api_client_id";
    public const string ScopeClaim = "yojana:api_scope";
    public const string DisclosureTierClaim = "yojana:api_disclosure_tier";
    public const string RateLimitClaim = "yojana:api_rate_limit";
    public const string SandboxClaim = "yojana:api_sandbox";
}

public sealed class PublicApiAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeveloperDbContext db,
    PlatformDbContext platform,
    ITenantContextSetter tenantSetter,
    IPortalPartyContextSetter portalPartySetter,
    IClock clock,
    SecurityEventWriter securityEvents,
    SecurityEventTenantResolver securityEventTenants,
    ApiAuthenticationRefusalLimiter refusalLimiter)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Items.TryGetValue(PreAuthenticationResultItem, out var cached)
            && cached is AuthenticateResult cachedResult) return cachedResult;
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
            return AuthenticateResult.NoResult();

        var token = header.Parameter;
        using var attempt = refusalLimiter.Acquire(Context.Connection.RemoteIpAddress?.ToString());
        if (!attempt.Allowed)
        {
            Context.Items[ThrottleRetryAfterItem] = attempt.RetryAfter;
            return AuthenticateResult.Fail("api_auth_throttled");
        }
        if (!TryTenant(token, out var tenantId))
            return await RefusedAsync("api_token_format_invalid", null, attempt);

        try { tenantSetter.Set(tenantId); }
        catch (InvalidOperationException)
        {
            return await RefusedAsync("api_token_tenant_mismatch", tenantId, attempt);
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        var stored = await db.AccessTokens.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId
            && row.TokenHash == hash, Context.RequestAborted);
        if (stored is null) return await RefusedAsync("api_token_unknown", tenantId, attempt);
        if (stored.ExpiresAt <= clock.UtcNow) return await RefusedAsync("api_token_expired", tenantId, attempt);
        if (stored.RevokedAt is not null) return await RefusedAsync("api_token_revoked", tenantId, attempt);

        var client = await db.ApiClients.SingleOrDefaultAsync(row => row.TenantId == tenantId
            && row.ClientId == stored.ClientId, Context.RequestAborted);
        if (client is null) return await RefusedAsync("api_client_unknown", tenantId, attempt);
        if (client.Status == ApiClientService.Revoked)
            return await RefusedAsync("api_client_revoked", tenantId, attempt);
        if (client.ExpiresAt is { } expires && expires <= clock.UtcNow)
            return await RefusedAsync("api_client_expired", tenantId, attempt);

        // COUPLING (SQL portal-party filter): setting the party before Serialisation opens a connection is what makes
        // TenantSessionContextInterceptor put portal_party_ref into SESSION_CONTEXT. The explicit ownership checks in
        // IPublicApiReadPort are still authoritative; removing this call loses only the database defence in depth.
        if (client.CustomerPartyRef is { } customerPartyId)
            portalPartySetter.SetPortalParty(customerPartyId);

        if (client.LastUsedAt is null || client.LastUsedAt <= clock.UtcNow.AddMinutes(-1))
        {
            client.LastUsedAt = clock.UtcNow;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var publicId = ApiClientService.PublicId(tenantId, client.ClientId);
        var claims = new List<Claim>
        {
            new(YojanaClaim.TenantId, tenantId.Value.ToString("D")),
            new(PublicApiAuthentication.ClientIdClaim, publicId),
            new(PublicApiAuthentication.DisclosureTierClaim, client.DisclosureTier),
            new(PublicApiAuthentication.RateLimitClaim,
                client.RateLimitPerMinute.ToString(CultureInfo.InvariantCulture)),
            new(PublicApiAuthentication.SandboxClaim, client.Sandbox ? "true" : "false"),
            new(ClaimTypes.NameIdentifier, publicId),
        };
        var currentScopes = client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        claims.AddRange(stored.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(currentScopes.Contains)
            .Select(scope => new Claim(PublicApiAuthentication.ScopeClaim, scope)));
        if (client.CustomerPartyRef is { } party)
            claims.Add(new Claim(YojanaClaim.CustomerPartyRef, party.ToString("D")));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims,
            PublicApiAuthentication.Scheme, ClaimTypes.NameIdentifier, ClaimTypes.Role));
        attempt.Succeeded();
        return AuthenticateResult.Success(new AuthenticationTicket(principal, PublicApiAuthentication.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var retryAfter = Context.Items.TryGetValue(ThrottleRetryAfterItem, out var value)
            && value is TimeSpan delay ? delay : (TimeSpan?)null;
        var throttled = retryAfter is not null;
        Response.StatusCode = throttled ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/problem+json";
        if (throttled)
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter!.Value.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        await Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = throttled ? "API authentication throttled" : "Authentication required",
            status = Response.StatusCode,
            detail = throttled ? "Too many API authentication refusals came from this address."
                : "A valid Yojana API client bearer token is required.",
            code = throttled ? "api_auth_throttled" : "invalid_token",
        }, Context.RequestAborted);
    }

    private async Task<AuthenticateResult> RefusedAsync(string reason, TenantId? tenantId,
        ApiAuthenticationRefusalLimiter.ApiAuthenticationAttempt attempt)
    {
        attempt.Refused();
        var existingTenant = await securityEventTenants.ExistingAsync(tenantId, Context.RequestAborted);
        securityEvents.Add(SecurityEventKind.ApiTokenRefused, null, existingTenant, null,
            new SecurityRequestContext(Context.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent),
            reason);
        await platform.SaveChangesAsync(Context.RequestAborted);
        return AuthenticateResult.Fail(reason);
    }

    private const string ThrottleRetryAfterItem = "Yojana.PublicApi.AuthenticationThrottleRetryAfter";
    internal const string PreAuthenticationResultItem = "Yojana.PublicApi.PreAuthenticationResult";

    private static bool TryTenant(string token, out TenantId tenantId)
    {
        tenantId = default;
        var parts = token.Split('_', 3, StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "yjt" || !Guid.TryParseExact(parts[1], "N", out var parsed)
            || parts[2].Length != 43) return false;
        tenantId = new TenantId(parsed);
        return !tenantId.IsEmpty;
    }
}

/// <summary>
/// Runs the API-client scheme before the platform rate-limiter middleware needs the client-id claim. Cookies are
/// removed from public operations so the staff session can never become their identity; OpenAPI is the sole dual-auth
/// endpoint and retains its cookie by design.
/// </summary>
public sealed class PublicApiPreAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(DeveloperEndpoints.PublicPrefix))
        {
            await next(context);
            return;
        }

        var isDocumentation = context.Request.Path.Equals(DeveloperEndpoints.PublicPrefix + "/openapi.json",
            StringComparison.OrdinalIgnoreCase);
        var cookie = context.Request.Headers.Cookie.ToString();
        if (!isDocumentation) context.Request.Headers.Remove("Cookie");
        if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ",
                StringComparison.OrdinalIgnoreCase))
        {
            var result = await context.AuthenticateAsync(PublicApiAuthentication.Scheme);
            context.Items[PublicApiAuthenticationHandler.PreAuthenticationResultItem] = result;
            if (result.Principal is not null) context.User = result.Principal;
        }

        await next(context);
        if (!isDocumentation && cookie.Length > 0) context.Request.Headers.Cookie = cookie;
    }
}
