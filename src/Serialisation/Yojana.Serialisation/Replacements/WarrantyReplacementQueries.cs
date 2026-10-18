using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

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
        var data = await (from document in db.WarrantyReplacements.AsNoTracking()
                          join link in db.ReplacementLinks.AsNoTracking() on new { document.TenantId, document.WarrantyReplacementId } equals new { link.TenantId, link.WarrantyReplacementId }
                          where document.TenantId == tenantId && ids.Contains(document.WarrantyReplacementId)
                          select new { Document = document, Link = link }).ToListAsync(cancellationToken);
        var byId = data.ToDictionary(row => row.Document.WarrantyReplacementId);
        var rows = ids.Where(byId.ContainsKey).Select(id => Row(byId[id].Document, byId[id].Link)).ToList();
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
        var unitIds = new[] { l.OriginalUnitId, l.ReplacementUnitId };
        var current = await db.Current.AsNoTracking().Where(unit => unit.TenantId == tenantId && unitIds.Contains(unit.UnitId))
            .ToDictionaryAsync(unit => unit.UnitId, cancellationToken);
        var original = current.GetValueOrDefault(l.OriginalUnitId)
            ?? throw new InvalidOperationException($"Original unit {l.OriginalUnitId} disappeared from current state.");
        var replacement = current.GetValueOrDefault(l.ReplacementUnitId)
            ?? throw new InvalidOperationException($"Replacement unit {l.ReplacementUnitId} disappeared from current state.");
        var serials = await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken);
        var sourceEntitlement = await db.Entitlements.AsNoTracking().SingleAsync(row => row.TenantId == tenantId && row.EntitlementId == l.SourceEntitlementId, cancellationToken);
        var targetEntitlement = l.TargetEntitlementId is { } targetId
            ? await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.EntitlementId == targetId, cancellationToken)
            : null;
        var rootId = sourceEntitlement.RootEntitlementId ?? sourceEntitlement.EntitlementId;
        var chainLength = Math.Max(0, await db.Entitlements.AsNoTracking().CountAsync(row => row.TenantId == tenantId
            && (row.EntitlementId == rootId || row.RootEntitlementId == rootId), cancellationToken) - 1);
        var replacementGrade = replacement.ConditionGrade ?? ConditionGrades.New;
        var conditionPassed = ConditionAllowed(l.ReplacementReason, l.ReplacementConditionAllowed, replacementGrade);
        var consentRequired = ConsentRequired(l.ReplacementReason, l.ReplacementConditionAllowed, replacementGrade, d.ServiceCountry, d.ConsumerContract);
        var available = replacement.LifecycleStatus == LifecycleStatus.Released;
        var checks = new List<WarrantyReplacementCheck>
        {
            new("condition", "Condition allowed for this reason",
                $"{l.ReplacementConditionAllowed} · actual grade {replacementGrade}", conditionPassed ? "allowed" : "blocked", conditionPassed),
            new("consent", "Consumer consent for a refurbished unit",
                consentRequired ? "EU consumer contract · recorded consent required" : "not required for this unit and contract",
                consentRequired ? l.ConsentRefurbished ? "recorded" : "missing" : "not_required", !consentRequired || l.ConsentRefurbished),
            new("availability", "Replacement unit free to ship",
                $"lifecycle {replacement.LifecycleStatus} · custody {replacement.LocationKind ?? "not recorded"}", available ? "available" : "blocked", available),
        };
        var transfers = new List<WarrantyReplacementTransferRow>
        {
            new(sourceEntitlement.EntitlementId, sourceEntitlement.CoverageFamily, sourceEntitlement.Type, sourceEntitlement.UnitId,
                serials.GetValueOrDefault(sourceEntitlement.UnitId), sourceEntitlement.StartDate, sourceEntitlement.EndDate,
                targetEntitlement is null ? "will_close_replaced" : "closed_replaced",
                $"source cover · {sourceEntitlement.Status}"),
        };
        if (targetEntitlement is null)
            transfers.Add(new(null, sourceEntitlement.CoverageFamily, sourceEntitlement.Type, l.ReplacementUnitId,
                serials.GetValueOrDefault(l.ReplacementUnitId), sourceEntitlement.StartDate, null, "will_open_on_ship",
                "end date is computed by TransferEntitlement when the replacement ships"));
        else
            transfers.Add(new(targetEntitlement.EntitlementId, targetEntitlement.CoverageFamily, targetEntitlement.Type, targetEntitlement.UnitId,
                serials.GetValueOrDefault(targetEntitlement.UnitId), targetEntitlement.StartDate, targetEntitlement.EndDate, "opened_inherited",
                targetEntitlement.AccrualCohortId is null ? "no new accrual" : "accrual cohort recorded"));
        var customerAssetTag = await db.Identifiers.AsNoTracking().Where(identifier => identifier.TenantId == tenantId
                && identifier.UnitId == l.OriginalUnitId && identifier.Type == IdentifierType.CustomerAssetTag && identifier.ValidTo == null)
            .Select(identifier => identifier.RawValue).SingleOrDefaultAsync(cancellationToken);
        var row = Row(d, l);
        var invoice = d.OriginalInvoiceType is not null && d.OriginalInvoiceId is { } invoiceId ? new DocumentRef(d.OriginalInvoiceType, invoiceId, d.OriginalInvoiceLine) : null;
        var nonReturnCharge = l.BilledPriceBasis is not null && l.BilledAmount is { } billedAmount
            ? new WarrantyReplacementNonReturnCharge("billed", l.BilledPriceBasis, billedAmount, l.BilledOn)
            : l.NonReturnPriceBasis is not null && l.NonReturnAmount is { } agreedAmount
                ? new WarrantyReplacementNonReturnCharge("agreed", l.NonReturnPriceBasis, agreedAmount, null)
                : null;
        return new WarrantyReplacementDetail(row, l.SourceEntitlementId, l.TargetEntitlementId, l.ReplacementLinkId, l.PreviousReplacementLinkId,
            invoice, d.ReturnReasonCode, d.RestockingFeeRule, d.StockPostingReference,
            lines.Select(line => new WarrantyReplacementLineRow(line.LineId, line.Sequence, line.Kind, line.ItemId, line.Description,
                line.Quantity, line.UnitAmount, line.LineAmount, line.Currency, line.RevenueRule)).ToList(),
            Unit(original, serials.GetValueOrDefault(original.UnitId)), Unit(replacement, serials.GetValueOrDefault(replacement.UnitId)),
            original.OwnerPartyRef, customerAssetTag, d.ReplacementPolicyCode ?? l.PolicyApplied ?? "not_resolved", chainLength,
            nonReturnCharge, checks, transfers);
    }

    private static WarrantyReplacementUnitView Unit(UnitCurrent unit, string? serial) =>
        new(unit.UnitId, serial, unit.ItemId, unit.ConditionGrade ?? ConditionGrades.New, unit.LifecycleStatus, unit.LocationKind, unit.LocationId);

    private static WarrantyReplacementRow Row(WarrantyReplacement document, ReplacementLink link) =>
        new(document.WarrantyReplacementId, document.ReplacementNumber, document.SourceKind, document.SourceId, document.SourceNumber,
            link.OriginalUnitId, link.ReplacementUnitId, link.OriginalItemId, link.ReplacementItemId, link.ReplacementReason, link.State, document.Status,
            link.PricePolicy, link.DifferenceAmount, link.Currency, link.ReturnDue, link.NonReturnPriceBasis, link.ReplacementConditionAllowed,
            link.ReplacementCondition, link.ConsentRefurbished, link.PolicyApplied, link.ComputedEnd, document.RestockingFeeAmount,
            document.CreatedAt, document.CreatedBy, document.ShippedAt, document.ShippedBy);

    private static bool ConditionAllowed(string reason, string allowed, string actual)
    {
        try { WarrantyReplacementRules.ValidateCondition(reason, allowed, actual, "US", false, false); return true; }
        catch (CommandRefusedException) { return false; }
    }

    private static bool ConsentRequired(string reason, string allowed, string actual, string country, bool consumerContract)
    {
        if (!consumerContract || !ConditionGrades.IsRefurbished(actual)) return false;
        try { WarrantyReplacementRules.ValidateCondition(reason, allowed, actual, country, true, false); return false; }
        catch (CommandRefusedException exception) when (exception.Rule == "refurbished_consent_required") { return true; }
        catch (CommandRefusedException) { return false; }
    }
}
