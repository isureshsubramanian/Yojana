using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Paging;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

public sealed class WarrantyLineExceptionQueries(SerialisationDbContext db, ITenantContext tenant)
{
    private static readonly SortedPage<Data.Entities.WarrantyLineException, Guid> Pages = new("raised", row => row.ExceptionId,
        new SortColumn<Data.Entities.WarrantyLineException, DateTime>("raised", row => row.RaisedAt),
        new SortColumn<Data.Entities.WarrantyLineException, DateTime>("trigger", row => row.TriggerAt),
        new SortColumn<Data.Entities.WarrantyLineException, string>("status", row => row.Status));

    public async Task<WarrantyLineExceptionPage> PageAsync(string? status, string? sort, string? direction,
        string? cursor, int skip, int take, CancellationToken cancellationToken)
    {
        var paging = Pages.Parse(sort, direction, take, cursor, skip);
        var query = db.WarrantyLineExceptions.AsNoTracking().Where(e => e.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var rows = await paging.Apply(query).Take(paging.Take)
            .Select(e => new WarrantyLineExceptionRow(e.ExceptionId, new DocumentRef(e.DocumentType, e.DocumentId, e.DocumentLine), e.ItemId, e.Status, e.MissingInput, e.Detail, e.TriggerAt, e.RaisedAt, e.ResolvedAt)).ToListAsync(cancellationToken);
        return new(rows, total, paging.Skip, paging.Take);
    }

    public Task<WarrantyLineExceptionPage> PageAsync(string? status, int skip, int take, CancellationToken cancellationToken) =>
        PageAsync(status, null, null, null, skip, take, cancellationToken);
}
