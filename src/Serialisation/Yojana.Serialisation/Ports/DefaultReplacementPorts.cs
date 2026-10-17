using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Ports;

/// <summary>A replacement's source is an approved replacement claim or an authorised RMA line covered by the named entitlement.</summary>
public sealed class DefaultReplacementSourceQueryPort(SerialisationDbContext db, ITenantContext tenant) : IReplacementSourceQueryPort
{
    public async Task<ReplacementSource?> ResolveAsync(string kind, Guid id, Guid sourceEntitlementId, CancellationToken cancellationToken)
    {
        if (kind == ReplacementSourceKind.Rma) return await ResolveRmaAsync(id, sourceEntitlementId, cancellationToken);
        if (kind != ReplacementSourceKind.Claim) return null;
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.ClaimId == id, cancellationToken);
        if (claim is null) return null;
        if (claim.Remedy != ClaimRemedy.Replacement || claim.Status is not (ClaimStatus.Approved or ClaimStatus.PartiallyApproved or ClaimStatus.Settled or ClaimStatus.Closed)) return null;
        var entitlementIds = JsonSerializer.Deserialize<List<Guid>>(claim.EntitlementIds, LedgerJson.Options) ?? [];
        if (!entitlementIds.Contains(sourceEntitlementId)) return null;
        var entitlement = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.EntitlementId == sourceEntitlementId, cancellationToken);
        if (entitlement is null || entitlement.UnitId != claim.UnitId) return null;
        var sale = entitlement.SourceDocumentType is not null && entitlement.SourceDocumentId is { } documentId
            ? new DocumentRef(entitlement.SourceDocumentType, documentId, entitlement.SourceDocumentLine)
            : null;
        return new(kind, id, claim.ClaimNumber, claim.UnitId, sourceEntitlementId, claim.ClaimDate, sale, null);
    }

    // The link binds to the unit actually received (MOD-08 invariant 2): once a line is received, only an accepted
    // receipt resolves, and the received unit is the original; before receipt (advance replacement) it is the authorised one.
    private async Task<ReplacementSource?> ResolveRmaAsync(Guid rmaId, Guid sourceEntitlementId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.RmaId == rmaId, cancellationToken);
        if (rma?.RmaNumber is null || rma.Stage is RmaStage.Requested or RmaStage.Closed) return null;
        var line = await db.RmaLines.AsNoTracking().Where(row => row.TenantId == tenantId && row.RmaId == rmaId && row.EntitlementId == sourceEntitlementId)
            .OrderBy(row => row.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (line?.AuthorisedUnitId is not { } authorised) return null;
        if (line.ReceiptOutcome is not null && line.ReceiptOutcome != ReturnOutcome.Accepted) return null;
        var original = line.ReceivedUnitId ?? authorised;
        var sale = new DocumentRef(line.OriginalSaleDocumentType, line.OriginalSaleDocumentId, line.OriginalSaleDocumentLine);
        return new(ReplacementSourceKind.Rma, rmaId, rma.RmaNumber, original, sourceEntitlementId, rma.RequestDate, sale, null);
    }
}

/// <summary>Quotes the restocking fee from the tenant's return reason; an unknown, inactive or absent reason carries no fee.</summary>
public sealed class DefaultReturnReasonQueryPort(SerialisationDbContext db, ITenantContext tenant) : IReturnReasonQueryPort
{
    public async Task<RestockingFeeQuote> QuoteAsync(string? returnReasonCode, decimal basis, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(returnReasonCode)) return new(RestockFeeRule.None, 0m);
        var reason = await db.ReturnReasons.AsNoTracking()
            .FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.Code == returnReasonCode && row.Active, cancellationToken);
        var value = reason?.RestockFeeValue ?? 0m;
        return reason?.RestockFeeRule switch
        {
            RestockFeeRule.Fixed => new($"{RestockFeeRule.Fixed}:{returnReasonCode}", value),
            RestockFeeRule.Percentage => new($"{RestockFeeRule.Percentage}:{returnReasonCode}", Math.Round(basis * value / 100m, 2, MidpointRounding.AwayFromZero)),
            _ => new(RestockFeeRule.None, 0m),
        };
    }
}
