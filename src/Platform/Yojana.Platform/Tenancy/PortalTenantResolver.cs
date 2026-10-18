using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Tenancy;

public sealed class PortalTenantResolver(PlatformDbContext db) : IPortalTenantResolver
{
    public async Task<TenantId?> ResolveCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalised = code.Trim().ToLowerInvariant();
        var value = await db.Tenants.AsNoTracking().Where(row => row.IsActive && row.Code == normalised)
            .Select(row => (Guid?)row.Id.Value).SingleOrDefaultAsync(cancellationToken);
        return value is { } id ? new TenantId(id) : null;
    }
}
