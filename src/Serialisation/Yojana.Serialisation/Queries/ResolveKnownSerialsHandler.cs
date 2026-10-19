using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Queries;

public static class ResolveKnownSerialsHandler
{
    public static async Task<ResolveKnownSerialsResult> Handle(ResolveKnownSerials query,
        SerialisationDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var requested = query.Serials.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var raw = await db.Identifiers.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ValidTo == null
            && requested.Contains(x.RawValue)).Select(x => x.RawValue).ToListAsync(cancellationToken);
        var known = raw.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(known, requested.Where(x => !known.Contains(x)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
