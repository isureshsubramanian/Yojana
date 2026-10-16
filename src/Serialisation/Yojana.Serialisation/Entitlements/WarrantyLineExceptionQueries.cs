using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

public sealed class WarrantyLineExceptionQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<WarrantyLineExceptionPage> PageAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.WarrantyLineExceptions.AsNoTracking().Where(e => e.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(e => e.RaisedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200))
            .Select(e => new WarrantyLineExceptionRow(e.ExceptionId, new DocumentRef(e.DocumentType, e.DocumentId, e.DocumentLine), e.ItemId, e.Status, e.MissingInput, e.Detail, e.TriggerAt, e.RaisedAt, e.ResolvedAt)).ToListAsync(cancellationToken);
        return new(rows, total, Math.Max(0, skip), Math.Clamp(take, 1, 200));
    }
}
