using Microsoft.EntityFrameworkCore;
using Quartz;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

public sealed class EntitlementRequirementQueries(SerialisationDbContext db, ITenantContext tenant, IPurchasingDocumentQueryPort purchasing, IItemPolicyPort policies)
{
    public async Task<EntitlementRequirementPage> PageAsync(string? status, string? documentType, Guid? documentId, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip); take = Math.Clamp(take, 1, 200);
        var q = db.EntitlementRequirements.AsNoTracking().Where(r => r.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(documentType)) q = q.Where(r => r.SourceDocumentType == documentType);
        if (documentId is { } id) q = q.Where(r => r.SourceDocumentId == id);
        var total = await q.CountAsync(cancellationToken);
        var entities = await q.OrderByDescending(r => r.RaisedAt).ThenBy(r => r.RequirementId).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new(entities.Select(Row).ToList(), total, skip, take);
    }

    public async Task<EntitlementRequirementCheck> CheckAsync(string documentType, Guid documentId, CancellationToken cancellationToken)
    {
        var entities = await db.EntitlementRequirements.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.SourceDocumentType == documentType && r.SourceDocumentId == documentId
            && (r.Status == EntitlementRequirementStatus.Open || r.Status == EntitlementRequirementStatus.Exception)).OrderBy(r => r.UnitId).ToListAsync(cancellationToken);
        var rows = entities.Select(Row).ToList();
        var problems = new List<string>();
        var reference = new DocumentRef(documentType, documentId);
        if (await purchasing.GetVendorShipmentAsync(reference, cancellationToken) is { } shipment)
        {
            foreach (var line in shipment.Lines.Where(l => l.Serials.Count == 0))
            {
                if ((await policies.PoliciesForAsync(line.ItemId, cancellationToken)).Any(p => p.CoverageFamily == CoverageFamilyCode.Base && p.EntitlementRequiredOnSale))
                    problems.Add($"line {line.LineNo}: {EntitlementMissingInput.SerialsPendingAsn}");
            }
        }
        return new(rows.Count == 0 && problems.Count == 0, rows, problems);
    }

    private static EntitlementRequirementRow Row(EntitlementRequirement r) => new(r.RequirementId, r.UnitId, r.OwnershipEpisodeId, r.ItemId,
        r.SourceDocumentId is { } id && r.SourceDocumentType is { } type ? new DocumentRef(type, id, r.SourceDocumentLine) : null,
        r.Status, r.MissingInput, r.Detail, r.RaisedAt, r.ResolvedAt, r.EntitlementId);
}

public sealed class EntitlementRequirementSweep(SerialisationDbContext db, ITenantSettings settings, ITenantContext tenant, IClock clock, IMessageBus bus)
{
    internal const string WatermarkKey = "entitlements.requirement-sweep.watermark";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(WatermarkKey, cancellationToken);
        var watermark = DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTime.MinValue;
        var to = clock.UtcNow;
        var tenantId = tenant.TenantId;
        var gaps = await (from e in db.Episodes.AsNoTracking()
                          join c in db.Current.AsNoTracking() on new { e.TenantId, e.UnitId } equals new { c.TenantId, c.UnitId }
                          where e.TenantId == tenantId && e.Kind == EpisodeKind.Ownership && e.PartyType == PartyType.Customer && e.ValidFrom >= watermark && e.ValidFrom < to
                                && !db.Entitlements.Any(x => x.TenantId == tenantId && x.UnitId == e.UnitId && x.CoverageFamily == CoverageFamilyCode.Base && x.Status != EntitlementStatus.Closed)
                                && !db.EntitlementRequirements.Any(x => x.TenantId == tenantId && x.UnitId == e.UnitId && x.OwnershipEpisodeId == e.EpisodeId)
                          select new { Episode = e, c.ItemId }).ToListAsync(cancellationToken);
        foreach (var gap in gaps)
        {
            db.EntitlementRequirements.Add(new EntitlementRequirement
            {
                TenantId = tenantId, RequirementId = TimeOrderedId.New(to), UnitId = gap.Episode.UnitId, OwnershipEpisodeId = gap.Episode.EpisodeId,
                ItemId = gap.ItemId, SourceDocumentType = gap.Episode.OpeningDocumentType, SourceDocumentId = gap.Episode.OpeningDocumentId,
                SourceDocumentLine = gap.Episode.OpeningDocumentLine, Status = EntitlementRequirementStatus.Exception,
                MissingInput = gap.Episode.OpeningDocumentId is null ? EntitlementMissingInput.NoStartEvidence : EntitlementMissingInput.CreationFailed,
                Detail = "Daily reconciliation found customer ownership without base cover or a requirement row.", RaisedAt = to,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var retry = await db.EntitlementRequirements.AsNoTracking().Where(r => r.TenantId == tenantId && r.Status == EntitlementRequirementStatus.Exception).Select(r => r.RequirementId).ToListAsync(cancellationToken);
        foreach (var ids in retry.Chunk(500)) await bus.PublishAsync(new ResolveEntitlementRequirements(ids), new DeliveryOptions { TenantId = tenantId.ToString() });
        await settings.SetAsync(WatermarkKey, to.ToString("O"), cancellationToken);
    }
}

[DisallowConcurrentExecution]
public sealed class EntitlementRequirementJob(ITenantContextSetter setter, EntitlementRequirementSweep sweep) : TenantJob(setter)
{
    public const string JobName = "serialisation.entitlement_requirements";
    public const string Cron = "0 0 4 * * ?";
    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) => sweep.RunAsync(cancellationToken);
}
