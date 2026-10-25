namespace Yojana.Developer.Clients;

public sealed record CreateApiClientRequest(string Name, IReadOnlyList<string> Scopes, string DisclosureTier,
    Guid? CustomerPartyId = null, bool Sandbox = false, DateTime? ExpiresAt = null,
    int RateLimitPerMinute = ApiClientService.DefaultRateLimitPerMinute);
public sealed record UpdateApiClientRequest(IReadOnlyList<string> Scopes, int RateLimitPerMinute);
public sealed record ApiClientParty(string Code, string? Name);
public sealed record ApiClientView(string Id, string Name, IReadOnlyList<string> Scopes, string DisclosureTier,
    ApiClientParty? CustomerParty, bool Sandbox, string Status, int RateLimitPerMinute, DateTime? ExpiresAt,
    DateTime CreatedAt, string CreatedBy, string SecretHint, DateTime? SecretRotatedAt, DateTime? RevokedAt,
    string? RevokedBy, DateTime? LastUsedAt);
public sealed record ApiClientCreated(ApiClientView Client, string Secret);
public sealed record ApiClientSecretRotated(string ClientId, string Secret, DateTime PreviousSecretExpiresAt);
public sealed record ApiClientSummary(int Active, int Sandbox, int Revoked, int SecretsOlderThanOneYear,
    DateTime? LastUsedAt);
public sealed record ApiClientPage(IReadOnlyList<ApiClientView> Items, string? Next, ApiClientSummary Summary);
