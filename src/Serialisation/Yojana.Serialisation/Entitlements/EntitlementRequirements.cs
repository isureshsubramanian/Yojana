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
    // unit → owner of its live base entitlement (null owner when the cover names none)
    private readonly Dictionary<Guid, Guid?> covered = [];
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
            var live = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && chunk.Contains(e.UnitId) && e.CoverageFamily == CoverageFamilyCode.Base && e.Status != EntitlementStatus.Closed)
                .Select(e => new { e.UnitId, e.OwnerPartyRef }).ToListAsync(cancellationToken);
            foreach (var row in live) covered[row.UnitId] = row.OwnerPartyRef;
            foreach (var item in current.Select(c => c.ItemId).Distinct())
            {
                if ((await policies.PoliciesForAsync(item, cancellationToken)).Any(p => p.CoverageFamily == CoverageFamilyCode.Base && p.EntitlementRequiredOnSale))
                    required.UnionWith(current.Where(c => c.ItemId == item).Select(c => c.UnitId));
            }
        }
    }

    public async Task RaiseForAsync(CustodyEpisode episode, CancellationToken cancellationToken)
    {
        // Inter-company (cases 27, 110): ownership passing to a group entity is not a sale; cover waits for the first external one.
        if (episode.PartyType != PartyType.Customer || episode.PartyIsGroupMember) return;
        var tenantId = tenant.TenantId;
        await PreloadAsync([episode.UnitId], cancellationToken);
        var itemId = items[episode.UnitId];
        // A resale by the customer (case 45) is raised whatever the item's flag: the policy decides whether cover follows.
        var hasCover = covered.TryGetValue(episode.UnitId, out var owner);
        var resale = hasCover && owner is { } previous && previous != episode.PartyRef;
        if (!resale && (!required.Contains(episode.UnitId) || hasCover)) return;

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
            var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.EpisodeId == row.OwnershipEpisodeId, cancellationToken);
            if (live is not null && live.Direction == EntitlementDirection.Outbound && live.OwnerPartyRef is { } owner && owner != episode.PartyRef)
            {
                await ResaleAsync(row, live, episode, db, ledger, store, clock, cancellationToken);
                continue;
            }

            if (live is not null) { Satisfy(row, live.EntitlementId, clock.UtcNow); continue; }
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
            // Cases 27 and 110: a policy that starts at the first external sale starts at the unit's first ownership by a
            // customer outside the group, never at an inter-company transfer and never again at a later resale.
            evidence[StartTrigger.FirstExternalSale] = await db.Episodes.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UnitId == row.UnitId && e.Kind == EpisodeKind.Ownership && e.PartyType == PartyType.Customer && !e.PartyIsGroupMember)
                .OrderBy(e => e.ValidFrom).Select(e => e.ValidFrom).FirstAsync(cancellationToken);
            var document = new DocumentRef(episode.OpeningDocumentType!, episode.OpeningDocumentId!.Value, episode.OpeningDocumentLine);
            try
            {
                var command = new CreateEntitlement(row.RequirementId, row.UnitId, CoverageFamilyCode.Base, DateOnly.FromDateTime(episode.ValidFrom), obligor,
                    clock.UtcNow, "entitlement-requirement", evidence, OwnerPartyRef: episode.PartyRef, OwnerPartyType: episode.PartyType,
                    StartEvidenceRef: $"{document.Type}:{document.Id:N}", SourceDocument: document, CustomerPoRef: episode.CustomerPoRef);
                var created = await CreateEntitlementHandler.CreateAsync(command, db, ledger, resolver, store, zones, refurbishedSettings, tenant, clock, null, [], false, null, null, null, cancellationToken);
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

    /// <summary>
    /// Case 45: the unit passed from one customer to another. The live base entitlement's policy decides: transferable →
    /// the cover follows to the new owner (the previous owner is kept in the ledger, never shown to the new one);
    /// not transferable → it closes at the resale and the requirement is waived by policy; registration or a fee →
    /// an exception until <see cref="CompleteEntitlementTransfer"/> records it.
    /// </summary>
    private static async Task ResaleAsync(EntitlementRequirement row, Entitlement live, CustodyEpisode episode, SerialisationDbContext db, EntitlementLedger ledger, PolicyStore store, IClock clock, CancellationToken cancellationToken)
    {
        var version = await db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == live.TenantId && v.PolicyVersionId == live.PolicyVersionId, cancellationToken);
        var transferable = (await store.ReadTermsAsync(version, cancellationToken)).Transferable;
        switch (transferable)
        {
            case Transferability.Yes:
                await TransferOwnerAsync(row, live, episode.PartyRef!.Value, episode.PartyType!, episode.ValidFrom, null, ledger, clock, cancellationToken);
                break;
            case Transferability.No:
                EntitlementLedger.Transition(live, EntitlementEventType.Closed);
                live.ClosedReason = EntitlementClosedReason.NotTransferable;
                live.ClosedEffective = DateOnly.FromDateTime(episode.ValidFrom);
                live.ClosedAt = clock.UtcNow;
                await ledger.AppendAsync(live, EntitlementEventType.Closed, EntitlementLedger.Key(row.RequirementId, live.UnitId, EntitlementEventType.Closed, episode.ValidFrom), episode.ValidFrom, "entitlement-requirement",
                    new { entitlement_id = live.EntitlementId, coverage_family = live.CoverageFamily, reason = EntitlementClosedReason.NotTransferable, effective = live.ClosedEffective }, null, cancellationToken);
                row.Status = EntitlementRequirementStatus.Waived; row.WaivedBy = "policy"; row.WaiverReason = "The policy does not let cover follow a resale."; row.MissingInput = null; row.ResolvedAt = clock.UtcNow;
                break;
            case Transferability.RegistrationRequired:
                Except(row, EntitlementMissingInput.TransferRegistrationRequired, "The policy lets cover follow a resale once the new owner registers.");
                break;
            default:
                Except(row, EntitlementMissingInput.TransferFeeRequired, "The policy lets cover follow a resale once the transfer fee is paid.");
                break;
        }
    }

    internal static async Task TransferOwnerAsync(EntitlementRequirement row, Entitlement live, Guid newOwner, string newOwnerType, DateTime at, string? evidenceRef, EntitlementLedger ledger, IClock clock, CancellationToken cancellationToken)
    {
        var previous = live.OwnerPartyRef;
        EntitlementLedger.Transition(live, EntitlementEventType.OwnerTransferred);
        live.OwnerPartyRef = newOwner;
        live.OwnerPartyType = newOwnerType;
        await ledger.AppendAsync(live, EntitlementEventType.OwnerTransferred, EntitlementLedger.Key(row.RequirementId, live.UnitId, EntitlementEventType.OwnerTransferred, at), at, "entitlement-requirement",
            new { entitlement_id = live.EntitlementId, coverage_family = live.CoverageFamily, owner_party_ref = newOwner, previous_owner_party_ref = previous, evidence_ref = evidenceRef }, null, cancellationToken);
        Satisfy(row, live.EntitlementId, clock.UtcNow);
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

/// <summary>Records the registration or fee a transferable-on-condition policy asks for, and moves the cover to the new owner (case 45).</summary>
public static class CompleteEntitlementTransferHandler
{
    public static async Task<EntitlementRequirementRow> Handle(CompleteEntitlementTransfer command, SerialisationDbContext db, EntitlementLedger ledger, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.EntitlementRequirements.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequirementId == command.RequirementId, cancellationToken)
            ?? throw new CommandRefusedException("requirement_unknown", $"Entitlement requirement {command.RequirementId} does not exist.");
        if (row.Status == EntitlementRequirementStatus.Satisfied) return EntitlementRequirementQueries.ToRow(row);
        if (row.MissingInput is not (EntitlementMissingInput.TransferRegistrationRequired or EntitlementMissingInput.TransferFeeRequired))
        {
            throw new CommandRefusedException("not_a_pending_transfer", "This requirement is not waiting for a transfer registration or fee.");
        }

        if (string.IsNullOrWhiteSpace(command.EvidenceRef)) throw new CommandRefusedException("evidence_required", "Record the registration or the fee receipt.");
        var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.EpisodeId == row.OwnershipEpisodeId, cancellationToken);
        var live = await db.Entitlements.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UnitId == row.UnitId && e.CoverageFamily == CoverageFamilyCode.Base && e.Status != EntitlementStatus.Closed, cancellationToken)
            ?? throw new CommandRefusedException("no_live_entitlement", "The unit has no live base entitlement to transfer.");
        await ResolveEntitlementRequirementsHandler.TransferOwnerAsync(row, live, episode.PartyRef!.Value, episode.PartyType!, command.EventTime, command.EvidenceRef, ledger, clock, cancellationToken);
        return EntitlementRequirementQueries.ToRow(row);
    }
}
