using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yojana.Developer.Clients;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Developer.Authentication;

public sealed record TokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    string? Scope = null);
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    string Scope);

public sealed class TokenRefusedException(string code, string message, int statusCode = 401) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class TokenService(
    DeveloperDbContext db,
    PlatformDbContext platform,
    ITenantContextSetter tenantSetter,
    IClock clock,
    SecurityEventWriter securityEvents,
    SecurityEventTenantResolver securityEventTenants)
{
    public async Task<TokenResponse> IssueAsync(TokenRequest request, SecurityRequestContext requestContext,
        ApiAuthenticationRefusalLimiter.ApiAuthenticationAttempt attempt, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.GrantType, "client_credentials", StringComparison.Ordinal))
            throw await RefusedAsync("unsupported_grant_type", "Only client_credentials is supported.", null,
                requestContext, StatusCodes.Status400BadRequest, attempt, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret)
            || !TryClientId(request.ClientId, out var tenantId, out var clientId))
            throw await RefusedAsync("invalid_client", "The client credentials were rejected.", null,
                requestContext, StatusCodes.Status401Unauthorized, attempt, cancellationToken);
        try { tenantSetter.Set(tenantId); }
        catch (InvalidOperationException)
        {
            throw await RefusedAsync("api_client_tenant_mismatch", "The client credentials were rejected.",
                tenantId, requestContext, StatusCodes.Status401Unauthorized, attempt, cancellationToken);
        }

        var client = await db.ApiClients.SingleOrDefaultAsync(row => row.TenantId == tenantId
            && row.ClientId == clientId, cancellationToken);
        if (client is null || !ApiClientService.SecretMatches(client, request.ClientSecret, clock.UtcNow))
            throw await RefusedAsync("invalid_client", "The client credentials were rejected.", tenantId,
                requestContext, StatusCodes.Status401Unauthorized, attempt, cancellationToken);
        if (client.Status == ApiClientService.Revoked)
            throw await RefusedAsync("api_client_revoked", "The API client has been revoked.", tenantId,
                requestContext, StatusCodes.Status401Unauthorized, attempt, cancellationToken);
        if (client.ExpiresAt is { } clientExpiry && clientExpiry <= clock.UtcNow)
            throw await RefusedAsync("api_client_expired", "The API client has expired.", tenantId,
                requestContext, StatusCodes.Status401Unauthorized, attempt, cancellationToken);

        var available = client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var requested = string.IsNullOrWhiteSpace(request.Scope) ? available.Order(StringComparer.Ordinal).ToList()
            : request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (requested.Count == 0 || requested.Any(scope => !available.Contains(scope)))
            throw await RefusedAsync("invalid_scope", "The requested scope is not granted to this client.",
                tenantId, requestContext, StatusCodes.Status400BadRequest, attempt, cancellationToken);

        var token = $"yjt_{tenantId.Value:N}_{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        db.AccessTokens.Add(new ApiAccessToken
        {
            TenantId = tenantId,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            ClientId = clientId,
            Scopes = string.Join(' ', requested),
            IssuedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync(cancellationToken);
        securityEvents.Add(SecurityEventKind.ApiTokenIssued, null, tenantId, null, requestContext,
            "api_token_issued");
        await platform.SaveChangesAsync(cancellationToken);
        attempt.Succeeded();
        return new(token, "Bearer", 3600, string.Join(' ', requested));
    }

    private async Task<TokenRefusedException> RefusedAsync(string code, string message, TenantId? tenantId,
        SecurityRequestContext requestContext, int statusCode,
        ApiAuthenticationRefusalLimiter.ApiAuthenticationAttempt attempt, CancellationToken cancellationToken)
    {
        attempt.Refused();
        var existingTenant = await securityEventTenants.ExistingAsync(tenantId, cancellationToken);
        securityEvents.Add(SecurityEventKind.ApiTokenRefused, null, existingTenant, null, requestContext, code);
        await platform.SaveChangesAsync(cancellationToken);
        return new(code, message, statusCode);
    }

    public async Task RecordRefusalAsync(string code, SecurityRequestContext requestContext,
        ApiAuthenticationRefusalLimiter.ApiAuthenticationAttempt attempt, CancellationToken cancellationToken)
    {
        attempt.Refused();
        securityEvents.Add(SecurityEventKind.ApiTokenRefused, null, null, null, requestContext, code);
        await platform.SaveChangesAsync(cancellationToken);
    }

    internal static bool TryClientId(string value, out TenantId tenantId, out Guid clientId)
    {
        tenantId = default;
        clientId = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('_', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "yjc" || !Guid.TryParseExact(parts[1], "N", out var tenant)
            || !Guid.TryParseExact(parts[2], "N", out clientId)) return false;
        tenantId = new TenantId(tenant);
        return !tenantId.IsEmpty && clientId != Guid.Empty;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');
}
