using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Replacements;

public sealed class WarrantyReplacementQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<WarrantyReplacementPage> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var source = db.WarrantyReplacements.AsNoTracking().Where(row => row.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) source = source.Where(row => row.Status == status);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var total = await source.CountAsync(cancellationToken);
        var ids = await source.OrderByDescending(row => row.CreatedAt).ThenBy(row => row.WarrantyReplacementId)
            .Skip(skip).Take(take).Select(row => row.WarrantyReplacementId).ToListAsync(cancellationToken);
        var rows = new List<WarrantyReplacementRow>(ids.Count);
        foreach (var id in ids) rows.Add((await DetailAsync(db, tenantId, id, cancellationToken)).Document);
        return new(rows, total, skip, take);
    }

    public Task<WarrantyReplacementDetail?> DetailAsync(Guid id, CancellationToken cancellationToken) => DetailOrNullAsync(db, tenant.TenantId, id, cancellationToken);

    internal static async Task<WarrantyReplacementDetail> DetailAsync(SerialisationDbContext db, TenantId tenantId, Guid id, CancellationToken cancellationToken) =>
        await DetailOrNullAsync(db, tenantId, id, cancellationToken) ?? throw new InvalidOperationException($"Warranty replacement {id} disappeared.");

    private static async Task<WarrantyReplacementDetail?> DetailOrNullAsync(SerialisationDbContext db, TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        var data = await (from document in db.WarrantyReplacements.AsNoTracking()
                          join link in db.ReplacementLinks.AsNoTracking() on new { document.TenantId, document.WarrantyReplacementId } equals new { link.TenantId, link.WarrantyReplacementId }
                          where document.TenantId == tenantId && document.WarrantyReplacementId == id
                              && link.State != ReplacementLinkState.Corrected
                          select new { Document = document, Link = link }).FirstOrDefaultAsync(cancellationToken);
        if (data is null) return null;
        var lines = await db.WarrantyReplacementLines.AsNoTracking().Where(row => row.TenantId == tenantId && row.WarrantyReplacementId == id)
            .OrderBy(row => row.Sequence).Select(row => new { row.LineId, row.Sequence, row.Kind, row.ItemId, row.Description, row.Quantity, row.UnitAmount, row.LineAmount, row.Currency, row.RevenueRule })
            .ToListAsync(cancellationToken);
        var d = data.Document;
        var l = data.Link;
        var row = new WarrantyReplacementRow(d.WarrantyReplacementId, d.ReplacementNumber, d.SourceKind, d.SourceId, d.SourceNumber,
            l.OriginalUnitId, l.ReplacementUnitId, l.OriginalItemId, l.ReplacementItemId, l.ReplacementReason, l.State, d.Status,
            l.PricePolicy, l.DifferenceAmount, l.Currency, l.ReturnDue, l.NonReturnPriceBasis, l.ReplacementConditionAllowed,
            l.ReplacementCondition, l.ConsentRefurbished, l.PolicyApplied, l.ComputedEnd, d.RestockingFeeAmount, d.CreatedAt, d.CreatedBy, d.ShippedAt, d.ShippedBy);
        var invoice = d.OriginalInvoiceType is not null && d.OriginalInvoiceId is { } invoiceId ? new DocumentRef(d.OriginalInvoiceType, invoiceId, d.OriginalInvoiceLine) : null;
        return new WarrantyReplacementDetail(row, l.SourceEntitlementId, l.TargetEntitlementId, l.ReplacementLinkId, l.PreviousReplacementLinkId,
            invoice, d.ReturnReasonCode, d.RestockingFeeRule, d.StockPostingReference,
            lines.Select(line => new WarrantyReplacementLineRow(line.LineId, line.Sequence, line.Kind, line.ItemId, line.Description,
                line.Quantity, line.UnitAmount, line.LineAmount, line.Currency, line.RevenueRule)).ToList());
    }
}
