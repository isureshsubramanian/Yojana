using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Developer.Authentication;

/// <summary>Keeps untrusted tenant ids off foreign-keyed audit rows, with at most one registry lookup per request.</summary>
public sealed class SecurityEventTenantResolver(PlatformDbContext db)
{
    private readonly Dictionary<TenantId, bool> cache = [];

    public async Task<TenantId?> ExistingAsync(TenantId? tenantId, CancellationToken cancellationToken)
    {
        if (tenantId is not { } candidate) return null;
        if (!cache.TryGetValue(candidate, out var exists))
        {
            exists = await db.Tenants.AsNoTracking().AnyAsync(row => row.Id == candidate, cancellationToken);
            cache[candidate] = exists;
        }
        return exists ? candidate : null;
    }
}
