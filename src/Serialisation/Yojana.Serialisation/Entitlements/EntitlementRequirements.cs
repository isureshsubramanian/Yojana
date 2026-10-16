using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public static class EntitlementRequirementRules
{
    public static string? MissingInput(bool hasOpeningDocument, bool hasObligor) =>
        !hasOpeningDocument ? EntitlementMissingInput.NoStartEvidence : !hasObligor ? EntitlementMissingInput.CreationFailed : null;
}

/// <summary>
/// The narrow seam between ownership and entitlement creation. HIDDEN COUPLING with EpisodeService: every customer
/// ownership opening must call this before its transaction commits; separating the calls permits a silent sale gap.
/// </summary>
public sealed class EntitlementRequirementWriter(SerialisationDbContext db, IItemPolicyPort policies, IMessageBus bus, ITenantContext tenant, IClock clock)
{
    private readonly Dictionary<Guid, Guid> items = [];
    private readonly HashSet<Guid> required = [];
    private readonly HashSet<Guid> covered = [];
    private readonly HashSet<(string Type, Guid Id)> documentsSent = [];

    /// <summary>Loads the sale decision for a whole document so ownership opening performs no per-unit reads.</summary>
    public async Task PreloadAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        var missing = unitIds.Where(id => !items.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0) return;
        var tenantId = tenant.TenantId;
        foreach (var chunk in missing.Chunk(2000))
        {
            var current = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && chunk.Contains(c.UnitId)).Select(c => new { c.UnitId, c.ItemId }).ToListAsync(cancellationToken);
            foreach (var row in current) items[row.UnitId] = row.ItemId;
            var live = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && chunk.Contains(e.UnitId) && e.CoverageFamily == CoverageFamilyCode.Base && e.Status != EntitlementStatus.Closed).Select(e => e.UnitId).ToListAsync(cancellationToken);
            covered.UnionWith(live);
            foreach (var item in current.Select(c => c.ItemId).Distinct())
            {
                if ((await policies.PoliciesForAsync(item, cancellationToken)).Any(p => p.CoverageFamily == CoverageFamilyCode.Base && p.EntitlementRequiredOnSale))
                    required.UnionWith(current.Where(c => c.ItemId == item).Select(c => c.UnitId));
            }
        }
    }

    public async Task RaiseForAsync(CustodyEpisode episode, CancellationToken cancellationToken)
    {
        if (episode.PartyType != PartyType.Customer) return;
        var tenantId = tenant.TenantId;
        await PreloadAsync([episode.UnitId], cancellationToken);
        var itemId = items[episode.UnitId];
        if (!required.Contains(episode.UnitId) || covered.Contains(episode.UnitId)) return;

        var local = db.ChangeTracker.Entries<EntitlementRequirement>().Select(e => e.Entity)
            .FirstOrDefault(r => r.TenantId == tenantId && r.UnitId == episode.UnitId && r.OwnershipEpisodeId == episode.EpisodeId);
        if (local is not null) return;

        var row = new EntitlementRequirement
        {
            TenantId = tenantId, RequirementId = TimeOrderedId.New(episode.ValidFrom), UnitId = episode.UnitId,
            OwnershipEpisodeId = episode.EpisodeId, ItemId = itemId, SourceDocumentType = episode.OpeningDocumentType,
            SourceDocumentId = episode.OpeningDocumentId, SourceDocumentLine = episode.OpeningDocumentLine,
            Status = EntitlementRequirementStatus.Open, RaisedAt = clock.UtcNow,
        };
        db.EntitlementRequirements.Add(row);
        // One follow-on per source document: the resolver picks up every open row the document raised in this transaction.
        var options = new DeliveryOptions { TenantId = tenantId.ToString() };
        if (row.SourceDocumentType is { } type && row.SourceDocumentId is { } id)
        {
            if (documentsSent.Add((type, id))) await bus.PublishAsync(new ResolveEntitlementRequirements([], new DocumentRef(type, id)), options);
        }
        else
        {
            await bus.PublishAsync(new ResolveEntitlementRequirements([row.RequirementId]), options);
        }
    }
}

public static class ResolveEntitlementRequirementsHandler
{
    // RefurbishedGoodsSettings is registered through a factory (SerialisationModule) so PlatformDbContext stays out of this graph.
    public static async Task Handle(ResolveEntitlementRequirements message, SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store,
        ITimeZonePort zones, RefurbishedGoodsSettings refurbishedSettings, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = message.RequirementIds.ToList();
        var rows = await db.EntitlementRequirements.Where(r => r.TenantId == tenantId && ids.Contains(r.RequirementId)
            && (r.Status == EntitlementRequirementStatus.Open || r.Status == EntitlementRequirementStatus.Exception)).ToListAsync(cancellationToken);
        if (message.SourceDocument is { } raisedBy)
        {
            var raised = await db.EntitlementRequirements.Where(r => r.TenantId == tenantId && r.SourceDocumentType == raisedBy.Type && r.SourceDocumentId == raisedBy.Id && r.Status == EntitlementRequirementStatus.Open)
                .ToListAsync(cancellationToken);
            rows.AddRange(raised.Where(r => rows.All(x => x.RequirementId != r.RequirementId)));
        }

        foreach (var row in rows)
        {
            var live = await db.Entitlements.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UnitId == row.UnitId && e.CoverageFamily == CoverageFamilyCode.Base && e.Status != EntitlementStatus.Closed, cancellationToken);
            if (live?.Status == EntitlementStatus.PendingEvidence)
            {
                Except(row, EntitlementMissingInput.NoStartEvidence, "The base entitlement is waiting for policy start evidence.");
                continue;
            }
            if (live is not null) { Satisfy(row, live.EntitlementId, clock.UtcNow); continue; }
            var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.EpisodeId == row.OwnershipEpisodeId, cancellationToken);
            var missing = EntitlementRequirementRules.MissingInput(episode.OpeningDocumentId is not null && episode.OpeningDocumentType is not null, episode.LegalEntityId is not null);
            if (missing == EntitlementMissingInput.NoStartEvidence)
            {
                Except(row, EntitlementMissingInput.NoStartEvidence, "The ownership episode has no opening document evidence.");
                continue;
            }
            if (missing is not null || episode.LegalEntityId is not { } obligor)
            {
                Except(row, EntitlementMissingInput.CreationFailed, "The ownership episode has no obligor legal entity.");
                continue;
            }

            var evidence = new Dictionary<string, DateTime>();
            if (episode.Source == EpisodeSource.Invoice) evidence[StartTrigger.Invoice] = episode.ValidFrom;
            else evidence[StartTrigger.Deliver] = episode.ValidFrom;
            var document = new DocumentRef(episode.OpeningDocumentType!, episode.OpeningDocumentId!.Value, episode.OpeningDocumentLine);
            try
            {
                var command = new CreateEntitlement(row.RequirementId, row.UnitId, CoverageFamilyCode.Base, DateOnly.FromDateTime(episode.ValidFrom), obligor,
                    clock.UtcNow, "entitlement-requirement", evidence, OwnerPartyRef: episode.PartyRef, OwnerPartyType: episode.PartyType,
                    StartEvidenceRef: $"{document.Type}:{document.Id:N}", SourceDocument: document, CustomerPoRef: episode.CustomerPoRef);
                var created = await CreateEntitlementHandler.CreateAsync(command, db, ledger, resolver, store, zones, refurbishedSettings, tenant, clock, null, [], false, null, null, cancellationToken);
                if (created.StartDate is null) Except(row, EntitlementMissingInput.NoStartEvidence, "The policy start chain does not accept the ownership evidence.");
                else Satisfy(row, created.EntitlementId, clock.UtcNow);
            }
            catch (CommandRefusedException ex) when (ex.Rule is "no_policy" or "policy_not_published")
            {
                Except(row, EntitlementMissingInput.NoPolicy, ex.Message);
            }
            catch (CommandRefusedException ex)
            {
                Except(row, EntitlementMissingInput.CreationFailed, ex.Message);
            }
        }
    }

    private static void Satisfy(EntitlementRequirement row, Guid entitlementId, DateTime at)
    {
        row.Status = EntitlementRequirementStatus.Satisfied; row.EntitlementId = entitlementId; row.MissingInput = null; row.Detail = null; row.ResolvedAt = at;
    }

    private static void Except(EntitlementRequirement row, string missing, string detail)
    {
        row.Status = EntitlementRequirementStatus.Exception; row.MissingInput = missing; row.Detail = detail.Length <= 1000 ? detail : detail[..1000]; row.ResolvedAt = null;
    }
}

public static class WaiveEntitlementRequirementHandler
{
    public static async Task<EntitlementRequirementRow> Handle(WaiveEntitlementRequirement command, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new CommandRefusedException("reason_required", "A waiver requires a reason.");
        var row = await db.EntitlementRequirements.SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequirementId == command.RequirementId, cancellationToken)
            ?? throw new CommandRefusedException("requirement_unknown", $"Entitlement requirement {command.RequirementId} does not exist.");
        if (row.Status == EntitlementRequirementStatus.Satisfied) throw new CommandRefusedException("already_satisfied", "A satisfied entitlement requirement cannot be waived.");
        row.Status = EntitlementRequirementStatus.Waived; row.WaivedBy = command.ActorRef; row.WaiverReason = command.Reason.Trim(); row.ResolvedAt = clock.UtcNow;
        var document = row.SourceDocumentId is { } id && row.SourceDocumentType is { } type ? new DocumentRef(type, id, row.SourceDocumentLine) : null;
        return new(row.RequirementId, row.UnitId, row.OwnershipEpisodeId, row.ItemId, document, row.Status, row.MissingInput, row.Detail, row.RaisedAt, row.ResolvedAt, row.EntitlementId);
    }
}
