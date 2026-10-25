using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>Read-only public-event context; Developer sees this contract, never Serialisation persistence.</summary>
public sealed class PublicEventContextPort(SerialisationDbContext db, ITenantContext tenant,
    IItemQueryPort items) : IPublicEventContextPort
{
    public async Task<PublicEventContext?> ForUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var current = await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId)
            .Select(row => new { row.ItemId, row.OwnerPartyRef }).SingleOrDefaultAsync(cancellationToken);
        if (current is null) return null;
        var serial = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId && row.ValidTo == null)
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0
                : row.Type == IdentifierType.ManufacturerSerial ? 1 : 2)
            .ThenBy(row => row.IdentifierId).Select(row => row.RawValue).FirstOrDefaultAsync(cancellationToken);
        var profile = await items.GetAsync(current.ItemId, cancellationToken);
        return new(profile?.ItemClass, current.OwnerPartyRef, serial);
    }

    public Task<string?> ReferenceAsync(string kind, Guid recordId, CancellationToken cancellationToken) => kind switch
    {
        "claim" => db.Claims.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ClaimId == recordId).Select(row => row.ClaimNumber).SingleOrDefaultAsync(cancellationToken),
        "rma" => db.Rmas.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.RmaId == recordId).Select(row => row.RmaNumber).SingleOrDefaultAsync(cancellationToken),
        "replacement" => db.WarrantyReplacements.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.WarrantyReplacementId == recordId).Select(row => row.ReplacementNumber).SingleOrDefaultAsync(cancellationToken),
        "certificate" => db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.CertificateId == recordId).Select(row => row.TrackingNumber).SingleOrDefaultAsync(cancellationToken),
        "campaign" => db.Campaigns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.CampaignId == recordId).Select(row => row.Code).SingleOrDefaultAsync(cancellationToken),
        "import" => db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.JobId == recordId).Select(row => row.FileName).SingleOrDefaultAsync(cancellationToken),
        _ => Task.FromResult<string?>(null),
    };

    public async Task<IReadOnlyList<Guid>> OperationUnitsAsync(string kind, Guid operationId,
        CancellationToken cancellationToken)
    {
        if (kind != "import") return [];
        return await db.ImportRowResults.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.JobId == operationId && row.UnitId != null && row.Outcome == ImportOutcome.Applied)
            .Select(row => row.UnitId!.Value).Distinct().Order().ToListAsync(cancellationToken);
    }
}
