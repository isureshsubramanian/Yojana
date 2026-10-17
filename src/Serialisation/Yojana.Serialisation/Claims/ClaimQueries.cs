using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

public sealed class ClaimQueries(SerialisationDbContext db, ITenantContext tenant) : IWarrantyClaimsPort
{
    public async Task<ClaimPage> ListAsync(string? status, DateOnly asOf, int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var source = db.Claims.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) source = source.Where(c => c.Status == status);
        var total = await source.CountAsync(cancellationToken);
        var rows = await source.OrderBy(c => c.Status == ClaimStatus.Closed).ThenBy(c => c.ClaimDate).ThenBy(c => c.ClaimId)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200))
            .Select(c => new { Claim = c, Requested = db.ClaimLines.Where(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId).Sum(l => l.RequestedAmount), Approved = db.ClaimLines.Where(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId).Sum(l => l.ApprovedAmount) })
            .ToListAsync(cancellationToken);
        return new(rows.Select(x => new ClaimListRow(x.Claim.ClaimId, x.Claim.UnitId, x.Claim.Status, x.Claim.CoverageSource, x.Claim.ClaimDate,
            x.Requested, x.Approved, Math.Max(0, asOf.DayNumber - x.Claim.ClaimDate.DayNumber), x.Claim.DenialReason)).ToList(), total, Math.Max(0, skip), Math.Clamp(take, 1, 200));
    }

    public async Task<ClaimDetail?> DetailAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClaimId == claimId, cancellationToken);
        if (claim is null) return null;
        var lines = await db.ClaimLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.ClaimId == claimId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var evidence = await db.ClaimEvidence.AsNoTracking().Where(e => e.TenantId == tenantId && e.ClaimId == claimId).OrderBy(e => e.RecordedAt).ThenBy(e => e.EvidenceId).ToListAsync(cancellationToken);
        return new ClaimDetail(claim.ClaimId, claim.UnitId, claim.Status, claim.RecoveryRaised, claim.ClaimDate, claim.ClaimantPartyRef, claim.SettlementPartyRef,
            claim.ServiceEntityId, claim.ObligorEntityId, claim.ServiceCountry, claim.Currency, claim.CoverageSource, claim.Remedy, claim.DenialReason, claim.ChainLength,
            JsonSerializer.Deserialize<List<Guid>>(claim.EntitlementIds, LedgerJson.Options) ?? [],
            lines.Select(Decision).ToList(), evidence.Select(e => new ClaimEvidenceRow(e.EvidenceId, e.Kind, e.Reference, e.Description, e.RecordedAt, e.ActorRef)).ToList(),
            claim.AdjudicatedBy, claim.ReviewerRole);
    }

    public Task<List<RecoveryClaimRow>> RecoveryListAsync(string? status, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = db.RecoveryClaims.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(r => r.Status == status);
        return rows.OrderBy(r => r.Status).ThenBy(r => r.RaisedAt).Select(r => new RecoveryClaimRow(r.RecoveryClaimId, r.CustomerClaimId, r.ClaimLineId,
            r.InboundEntitlementId, r.ChildUnitId, r.VendorPartyRef, r.RequestedAmount, r.ApprovedAmount, r.Currency, r.Status, r.RaisedAt, r.ApprovedAt, r.ApprovedBy)).ToListAsync(cancellationToken);
    }

    public Task<bool> HasOpenClaimsAsync(Guid unitId, Guid entitlementId, CancellationToken cancellationToken) =>
        HasOpenClaimsAsync(unitId, [entitlementId], cancellationToken);

    public async Task<bool> HasOpenClaimsAsync(Guid unitId, IReadOnlyList<Guid> entitlementIds, CancellationToken cancellationToken)
    {
        if (entitlementIds.Count == 0) return false;
        var tenantId = tenant.TenantId;
        return await db.Claims.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Status != ClaimStatus.Closed
            && (c.UnitId == unitId || db.ClaimLines.Any(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId && l.UnitId == unitId))
            && (c.Status == ClaimStatus.Submitted || c.Status == ClaimStatus.Adjudicating
                || db.ClaimLines.Any(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId && l.UnitId == unitId
                    && l.EntitlementId != null && entitlementIds.Contains(l.EntitlementId.Value))), cancellationToken);
    }

    internal static ClaimLineDecision Decision(Data.Entities.ClaimLine line) => new(line.ClaimLineId, line.UnitId, line.CoverageFamily, line.RequestedAmount,
        line.CoveragePct, line.ApprovedAmount, line.CoverageSource, line.EntitlementId, line.DenialReason, line.ChainLength);
}
