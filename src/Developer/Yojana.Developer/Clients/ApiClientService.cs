using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;
using Yojana.Serialisation.Contracts;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Developer.Clients;

public sealed class ApiClientService(
    DeveloperDbContext db,
    PlatformDbContext platform,
    ITenantContext tenant,
    IClock clock,
    IPartyNamePort parties,
    IUserDirectory users,
    SecurityEventWriter securityEvents)
{
    public const int DefaultRateLimitPerMinute = 600;
    public const int MaximumRateLimitPerMinute = 60_000;
    public const string Active = "active";
    public const string Revoked = "revoked";

    public async Task<ApiClientPage> PageAsync(string? sort, string? direction, string? cursor, int? take,
        CancellationToken cancellationToken)
    {
        var paging = ApiClientEndpoints.ApiClientPages.Parse(sort, direction, take, cursor);
        var rows = await paging.Apply(db.ApiClients.AsNoTracking().Where(row => row.TenantId == tenant.TenantId))
            .Take(paging.Take + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > paging.Take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var views = await ViewsAsync(rows, cancellationToken);
        var oneYearAgo = clock.UtcNow.AddYears(-1);
        var totals = await db.ApiClients.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .GroupBy(_ => 1).Select(group => new
            {
                Active = group.Count(row => row.Status == Active),
                Sandbox = group.Count(row => row.Status == Active && row.Sandbox),
                Revoked = group.Count(row => row.Status == Revoked),
                SecretsOlderThanOneYear = group.Count(row => row.Status == Active
                    && (row.SecretRotatedAt ?? row.CreatedAt) < oneYearAgo),
                LastUsedAt = group.Max(row => row.LastUsedAt),
            }).SingleOrDefaultAsync(cancellationToken);
        var summary = totals is null ? new ApiClientSummary(0, 0, 0, 0, null)
            : new ApiClientSummary(totals.Active, totals.Sandbox, totals.Revoked,
                totals.SecretsOlderThanOneYear, totals.LastUsedAt);
        return new(views, hasMore && rows.Count > 0 ? paging.CursorFor(rows[^1]) : null, summary);
    }

    public async Task<ApiClientView?> GetAsync(string publicId, CancellationToken cancellationToken)
    {
        var clientId = ParseClientId(publicId, tenant.TenantId);
        var row = await db.ApiClients.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenant.TenantId
            && item.ClientId == clientId, cancellationToken);
        return row is null ? null : (await ViewsAsync([row], cancellationToken))[0];
    }

    public async Task<ApiClientCreated> CreateAsync(CreateApiClientRequest request, Guid actorUserId,
        SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        var name = ValidateName(request.Name);
        var scopes = ValidateScopes(request.Scopes);
        ValidateTier(request.DisclosureTier, request.CustomerPartyId);
        ValidateRate(request.RateLimitPerMinute);
        if (request.ExpiresAt is { } expires && expires <= clock.UtcNow)
            throw new CommandRefusedException("api_client_expiry_invalid", "The API client expiry must be in the future.");
        if (await db.ApiClients.AnyAsync(row => row.TenantId == tenant.TenantId && row.Name == name,
                cancellationToken))
            throw new CommandRefusedException("api_client_name_taken", "An API client already uses this name.");

        var secret = NewSecret();
        var row = new ApiClient
        {
            TenantId = tenant.TenantId,
            ClientId = Guid.CreateVersion7(),
            Name = name,
            SecretHash = Hash(secret),
            SecretHint = secret[..8],
            Scopes = string.Join(' ', scopes),
            DisclosureTier = request.DisclosureTier,
            CustomerPartyRef = request.CustomerPartyId,
            Sandbox = request.Sandbox,
            Status = Active,
            RateLimitPerMinute = request.RateLimitPerMinute,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = clock.UtcNow,
            CreatedBy = actorUserId,
        };
        db.ApiClients.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        securityEvents.Add(SecurityEventKind.ApiClientCreated, null, tenant.TenantId, actorUserId, requestContext,
            "api_client_created");
        await platform.SaveChangesAsync(cancellationToken);
        return new((await ViewsAsync([row], cancellationToken))[0], secret);
    }

    public async Task<ApiClientSecretRotated> RotateAsync(string publicId, Guid actorUserId,
        SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        var row = await RequiredAsync(publicId, cancellationToken);
        EnsureActive(row);
        var secret = NewSecret();
        row.PreviousSecretHash = row.SecretHash;
        row.PreviousSecretExpiresAt = clock.UtcNow.AddHours(24);
        row.SecretHash = Hash(secret);
        row.SecretHint = secret[..8];
        row.SecretRotatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        securityEvents.Add(SecurityEventKind.ApiClientSecretRotated, null, tenant.TenantId, actorUserId, requestContext,
            "api_client_secret_rotated");
        await platform.SaveChangesAsync(cancellationToken);
        return new(PublicId(row.TenantId, row.ClientId), secret, row.PreviousSecretExpiresAt.Value);
    }

    public async Task<ApiClientView> RevokeAsync(string publicId, Guid actorUserId,
        SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        var row = await RequiredAsync(publicId, cancellationToken);
        if (row.Status != Revoked)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            row.Status = Revoked;
            row.RevokedAt = clock.UtcNow;
            row.RevokedBy = actorUserId;
            await db.AccessTokens.Where(token => token.TenantId == tenant.TenantId
                    && token.ClientId == row.ClientId && token.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedAt, clock.UtcNow),
                    cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            securityEvents.Add(SecurityEventKind.ApiClientRevoked, null, tenant.TenantId, actorUserId,
                requestContext, "api_client_revoked");
            await platform.SaveChangesAsync(cancellationToken);
        }
        return (await ViewsAsync([row], cancellationToken))[0];
    }

    public async Task<ApiClientView> UpdateAsync(string publicId, UpdateApiClientRequest request,
        Guid actorUserId, SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        var row = await RequiredAsync(publicId, cancellationToken);
        EnsureActive(row);
        row.Scopes = string.Join(' ', ValidateScopes(request.Scopes));
        ValidateRate(request.RateLimitPerMinute);
        row.RateLimitPerMinute = request.RateLimitPerMinute;
        await db.SaveChangesAsync(cancellationToken);
        securityEvents.Add(SecurityEventKind.ApiClientChanged, null, tenant.TenantId, actorUserId, requestContext,
            "api_client_access_updated");
        await platform.SaveChangesAsync(cancellationToken);
        return (await ViewsAsync([row], cancellationToken))[0];
    }

    public static string PublicId(TenantId tenantId, Guid clientId) =>
        $"yjc_{tenantId.Value:N}_{clientId:N}";

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static bool SecretMatches(ApiClient client, string supplied, DateTime now)
    {
        var suppliedHash = Hash(supplied);
        var currentMatches = CryptographicOperations.FixedTimeEquals(suppliedHash, client.SecretHash);
        var previousMatches = client.PreviousSecretHash is { } previous && client.PreviousSecretExpiresAt > now
            && CryptographicOperations.FixedTimeEquals(suppliedHash, previous);
        return currentMatches | previousMatches;
    }

    public static Guid ParseClientId(string publicId, TenantId expectedTenant)
    {
        var parts = publicId.Split('_', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "yjc" || !Guid.TryParseExact(parts[1], "N", out var tenantId)
            || !Guid.TryParseExact(parts[2], "N", out var clientId) || tenantId != expectedTenant.Value)
            throw new CommandRefusedException("api_client_id_invalid", "The API client id is invalid for this tenant.");
        return clientId;
    }

    private async Task<ApiClient> RequiredAsync(string publicId, CancellationToken cancellationToken)
    {
        var clientId = ParseClientId(publicId, tenant.TenantId);
        return await db.ApiClients.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.ClientId == clientId, cancellationToken)
            ?? throw new CommandRefusedException("api_client_unknown", "The API client was not found.");
    }

    private static void EnsureActive(ApiClient row)
    {
        if (row.Status == Revoked)
            throw new CommandRefusedException("api_client_revoked", "The API client has been revoked.");
    }

    private async Task<IReadOnlyList<ApiClientView>> ViewsAsync(IReadOnlyList<ApiClient> rows,
        CancellationToken cancellationToken)
    {
        var partyIds = rows.Select(row => row.CustomerPartyRef).OfType<Guid>().Distinct().ToList();
        var partyNames = partyIds.Count == 0 ? new Dictionary<Guid, PartyLabel>()
            : await parties.NamesAsync(partyIds, cancellationToken);
        var actorRefs = rows.SelectMany(row => new[] { row.CreatedBy, row.RevokedBy ?? Guid.Empty })
            .Where(id => id != Guid.Empty).Distinct().Select(id => id.ToString("D")).ToList();
        var actorNames = actorRefs.Count == 0 ? new Dictionary<string, string>()
            : await users.DisplayNamesAsync(actorRefs, cancellationToken);
        return rows.Select(row =>
        {
            var party = row.CustomerPartyRef is { } partyId && partyNames.TryGetValue(partyId, out var label)
                ? new ApiClientParty(label.Code, label.Name) : null;
            var createdRef = row.CreatedBy.ToString("D");
            var revokedRef = row.RevokedBy?.ToString("D");
            return new ApiClientView(PublicId(row.TenantId, row.ClientId), row.Name,
                row.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), row.DisclosureTier, party,
                row.Sandbox, row.Status, row.RateLimitPerMinute, row.ExpiresAt, row.CreatedAt,
                actorNames.GetValueOrDefault(createdRef, "Former tenant member"), row.SecretHint,
                row.SecretRotatedAt, row.RevokedAt, revokedRef is null ? null
                    : actorNames.GetValueOrDefault(revokedRef, "Former tenant member"), row.LastUsedAt);
        }).ToList();
    }

    private static string ValidateName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 120)
            throw new CommandRefusedException("api_client_name_invalid", "The API client name must be 1 to 120 characters.");
        return name;
    }

    private static List<string> ValidateScopes(IReadOnlyList<string> values)
    {
        var scopes = values.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
            throw new CommandRefusedException("api_client_scope_required", "Choose at least one public API scope.");
        var unknown = scopes.FirstOrDefault(scope => !PublicApiScopes.All.Contains(scope));
        if (unknown is not null)
            throw new CommandRefusedException("api_client_scope_unknown", $"Unknown public API scope '{unknown}'.");
        return scopes;
    }

    private static void ValidateTier(string tier, Guid? customerPartyId)
    {
        if (!PublicApiDisclosureTier.All.Contains(tier))
            throw new CommandRefusedException("api_client_tier_unknown", "The disclosure tier is unknown.");
        if (tier == PublicApiDisclosureTier.Customer && customerPartyId is null)
            throw new CommandRefusedException("api_client_customer_required",
                "A customer-tier client must be bound to a customer party.");
    }

    private static void ValidateRate(int rate)
    {
        if (rate is < 1 or > MaximumRateLimitPerMinute)
            throw new CommandRefusedException("api_client_rate_limit_invalid",
                $"The rate limit must be between 1 and {MaximumRateLimitPerMinute.ToString(CultureInfo.InvariantCulture)} requests per minute.");
    }

    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');

}
