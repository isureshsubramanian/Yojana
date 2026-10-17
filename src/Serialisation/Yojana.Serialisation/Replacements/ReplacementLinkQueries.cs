using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Replacements;

public sealed class ReplacementLinkQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<ReplacementLinkPage> PageAsync(string? state, bool overdue, int? dueWithinDays, string? query,
        DateOnly asOf, int skip, int take, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(state) && !ReplacementLinkState.All.Contains(state)) throw new ArgumentException($"Unknown replacement link state '{state}'.", nameof(state));
        if (dueWithinDays is < 1 or > 31) throw new ArgumentException("Due-within days must be between 1 and 31.", nameof(dueWithinDays));
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var tenantId = tenant.TenantId;
        var all = db.ReplacementLinks.AsNoTracking().Where(row => row.TenantId == tenantId);
        var awaiting = all.Where(row => row.State == ReplacementLinkState.AwaitingReturn);
        var kpis = new ReplacementLinkKpis(
            await awaiting.CountAsync(cancellationToken),
            await awaiting.CountAsync(row => row.ReturnDue < asOf, cancellationToken),
            await awaiting.CountAsync(row => row.ReturnDue >= asOf && row.ReturnDue <= asOf.AddDays(7), cancellationToken),
            await db.PartyReplacementHolds.AsNoTracking().CountAsync(row => row.TenantId == tenantId && row.LiftedAt == null, cancellationToken));

        var source = all;
        if (!string.IsNullOrWhiteSpace(state)) source = source.Where(row => row.State == state);
        if (overdue) source = source.Where(row => row.State == ReplacementLinkState.AwaitingReturn && row.ReturnDue < asOf);
        if (dueWithinDays is { } days) source = source.Where(row => row.State == ReplacementLinkState.AwaitingReturn
            && row.ReturnDue >= asOf && row.ReturnDue <= asOf.AddDays(days));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            source = source.Where(link =>
                db.WarrantyReplacements.Any(document => document.TenantId == tenantId
                    && document.WarrantyReplacementId == link.WarrantyReplacementId
                    && (document.ReplacementNumber.Contains(text) || document.SourceNumber.Contains(text)))
                || db.Identifiers.Any(identifier => identifier.TenantId == tenantId && identifier.ValidTo == null
                    && (identifier.UnitId == link.OriginalUnitId || identifier.UnitId == link.ReplacementUnitId)
                    && (identifier.RawValue.Contains(text) || identifier.NormalisedValue.Contains(text))));
        }
        var total = await source.CountAsync(cancellationToken);
        var data = await (from link in source
                          join document in db.WarrantyReplacements.AsNoTracking() on new { link.TenantId, link.WarrantyReplacementId } equals new { document.TenantId, document.WarrantyReplacementId }
                          orderby link.ReturnDue, link.ReplacementLinkId
                          select new
                          {
                              link.ReplacementLinkId, link.WarrantyReplacementId, document.ReplacementNumber, link.Direction, link.State,
                              link.OriginalUnitId, link.ReplacementUnitId, link.CounterpartyPartyRef, link.ReturnDue, link.NonReturnPriceBasis,
                              link.NonReturnAmount, link.Currency, link.OverduePublishedAt,
                              link.OriginalItemId, link.ReplacementItemId, link.SourceNumber,
                              link.ReplacementReason, document.ShippedAt,
                              OriginalSerial = db.Identifiers.Where(identifier => identifier.TenantId == tenantId
                                  && identifier.UnitId == link.OriginalUnitId && identifier.ValidTo == null
                                  && identifier.Type == IdentifierType.InternalSerial).Select(identifier => identifier.RawValue).FirstOrDefault(),
                              ReplacementSerial = db.Identifiers.Where(identifier => identifier.TenantId == tenantId
                                  && identifier.UnitId == link.ReplacementUnitId && identifier.ValidTo == null
                                  && identifier.Type == IdentifierType.InternalSerial).Select(identifier => identifier.RawValue).FirstOrDefault(),
                              PartyOnHold = link.CounterpartyPartyRef != null && db.PartyReplacementHolds.Any(hold => hold.TenantId == tenantId && hold.PartyRef == link.CounterpartyPartyRef && hold.LiftedAt == null),
                          }).Skip(skip).Take(take).ToListAsync(cancellationToken);
        var rows = data.Select(row => new ReplacementLinkRow(row.ReplacementLinkId, row.WarrantyReplacementId, row.ReplacementNumber,
            row.Direction, row.State, row.OriginalUnitId, row.ReplacementUnitId, row.CounterpartyPartyRef, row.ReturnDue,
            row.ReturnDue is { } due && due < asOf ? asOf.DayNumber - due.DayNumber : null, row.NonReturnPriceBasis,
            row.NonReturnAmount, row.Currency, row.OverduePublishedAt, row.PartyOnHold, row.OriginalSerial,
            row.ReplacementSerial, row.OriginalItemId, row.ReplacementItemId, row.SourceNumber,
            row.ReplacementReason, row.ShippedAt)).ToList();
        return new(rows, kpis, total, skip, take);
    }

    public async Task<PendingWarrantyReplacementPage> PendingAsync(int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var tenantId = tenant.TenantId;
        var source = from line in db.RmaLines.AsNoTracking()
                     join rma in db.Rmas.AsNoTracking() on new { line.TenantId, line.RmaId } equals new { rma.TenantId, rma.RmaId }
                     where line.TenantId == tenantId && line.Disposition == RmaDisposition.Replace && line.EntitlementId != null
                         && (line.ReceivedUnitId != null || line.AuthorisedUnitId != null) && rma.RmaNumber != null && rma.DispositionedAt != null
                         && !db.ReplacementLinks.Any(link => link.TenantId == tenantId && link.SourceKind == ReplacementSourceKind.Rma
                             && link.SourceId == line.RmaId && link.SourceEntitlementId == line.EntitlementId)
                     select new { line.RmaId, line.RmaLineId, rma.RmaNumber, OriginalUnitId = line.ReceivedUnitId ?? line.AuthorisedUnitId,
                         SourceEntitlementId = line.EntitlementId, line.CoverageFamily, rma.DispositionedAt };
        var total = await source.CountAsync(cancellationToken);
        var data = await source.OrderBy(row => row.DispositionedAt).ThenBy(row => row.RmaLineId).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new(data.Select(row => new PendingWarrantyReplacementRow(row.RmaId, row.RmaLineId, row.RmaNumber!, row.OriginalUnitId!.Value,
                row.SourceEntitlementId!.Value, row.CoverageFamily, row.DispositionedAt!.Value)).ToList(), total, skip, take);
    }
}
