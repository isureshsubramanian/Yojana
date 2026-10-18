using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Rmas;

/// <summary>Re-runs the shared claim-line policy decider only for work whose lifecycle is still open.</summary>
public static class EntitlementCoverageChangedHandler
{
    private static readonly string[] ClosedClaimStatuses = [ClaimStatus.Settled, ClaimStatus.Closed];
    private static readonly string[] ClosedRmaStages = [RmaStage.Dispositioned, RmaStage.Closed];

    /// <summary>
    /// The re-check runs in the background beside user commands, so it can lose an optimistic-concurrency race on a
    /// row a command is changing at the same moment. Without a retry that lost race sent the message to the error queue
    /// and the open work kept its old coverage decision silently. The whole re-check is one transaction and each work
    /// item is skipped once its decision for this source event exists, so a retry repeats only what did not commit.
    /// </summary>
    public static void Configure(HandlerChain chain) => chain.OnException<DbUpdateConcurrencyException>()
        .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));

    public static async Task Handle(EntitlementCoverageChanged message, SerialisationDbContext db, ClaimUnitFacts facts,
        ClaimLineDecider decider, IItemQueryPort items, ICampaignCoverPort campaigns, IServiceOrderQueryPort serviceOrders,
        IUnitEventWriter writer, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var drafts = new List<UnitEventDraft>();
        await ReAdjudicateRmasAsync(message, db, facts, decider, items, tenantId, drafts, cancellationToken);
        await ReAdjudicateClaimsAsync(message, db, facts, decider, items, campaigns, tenantId, drafts, cancellationToken);
        await ReAdjudicateServiceAsync(message, db, facts, decider, items, serviceOrders, tenantId, drafts, cancellationToken);
        await writer.AppendAsync(drafts, cancellationToken);
    }

    private static async Task ReAdjudicateRmasAsync(EntitlementCoverageChanged message, SerialisationDbContext db,
        ClaimUnitFacts facts, ClaimLineDecider decider, IItemQueryPort items, TenantId tenantId, List<UnitEventDraft> drafts,
        CancellationToken cancellationToken)
    {
        var rows = await (from line in db.RmaLines
                          join rma in db.Rmas on new { line.TenantId, line.RmaId } equals new { rma.TenantId, rma.RmaId }
                          where line.TenantId == tenantId && !ClosedRmaStages.Contains(rma.Stage)
                                && (line.RequestedUnitId == message.Header.UnitId || line.AuthorisedUnitId == message.Header.UnitId
                                    || line.ReceivedUnitId == message.Header.UnitId)
                          orderby rma.RmaId, line.Sequence
                          select new { Rma = rma, Line = line }).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (await ReplayedAsync(db, tenantId, message.Header.EventId, CoverageWorkType.Rma, row.Rma.RmaId, row.Line.RmaLineId, cancellationToken)) continue;
            var root = await facts.ReadAsync(row.Line.RequestedUnitId, cancellationToken);
            var item = await items.GetAsync(root.ItemId, cancellationToken);
            var claim = new Claim
            {
                TenantId = tenantId, ClaimId = row.Rma.RmaId, ClaimNumber = row.Rma.RmaNumber ?? "",
                CommandId = row.Rma.CommandId, UnitId = row.Line.RequestedUnitId, ClaimDate = row.Rma.RequestDate,
                ClaimantPartyRef = row.Rma.ReturningPartyRef, SettlementPartyRef = row.Rma.SettlementPartyRef,
                ServiceEntityId = row.Rma.ServiceEntityId, ObligorEntityId = row.Rma.ObligorEntityId,
                ServiceCountry = row.Rma.ServiceCountry, Currency = row.Rma.Currency, CoverageSource = ClaimCoverageSource.Chargeable,
                Remedy = ClaimRemedy.Repair, Status = ClaimStatus.Adjudicating, SubmittedAt = row.Rma.RequestedAt,
                SubmittedBy = row.Rma.RequestedBy,
            };
            var line = new ClaimLine
            {
                TenantId = tenantId, ClaimLineId = row.Line.RmaLineId, ClaimId = row.Rma.RmaId,
                UnitId = row.Line.RequestedUnitId, Sequence = row.Line.Sequence, Kind = row.Line.ClaimLineKind,
                CoverageFamily = row.Line.CoverageFamily, Description = row.Line.Description,
                RequestedAmount = row.Line.RequestedAmount, CoveragePct = row.Line.CoveragePct ?? 0,
                ApprovedAmount = row.Line.ApprovedAmount ?? 0, CoverageSource = row.Line.CoverageSource ?? ClaimCoverageSource.Chargeable,
                EntitlementId = row.Line.EntitlementId, PolicyVersionId = row.Line.PolicyVersionId,
                DenialReason = row.Line.DenialReason, Measurements = "{}",
            };
            var before = Snapshot(line);
            var outcome = await decider.DecideAsync(claim, root, item, line, null, cancellationToken);
            ClaimLineDecider.Apply(line, outcome);
            var after = Snapshot(line);
            row.Line.CoveragePct = line.CoveragePct;
            row.Line.ApprovedAmount = line.ApprovedAmount;
            row.Line.CoverageSource = line.CoverageSource;
            row.Line.EntitlementId = line.EntitlementId;
            row.Line.PolicyVersionId = line.PolicyVersionId;
            row.Line.DenialReason = line.DenialReason;
            row.Line.CoverageDecisionRef = AddDecision(message, db, tenantId, CoverageWorkType.Rma, row.Rma.RmaId,
                row.Line.RmaLineId, before, after, row.Line.Sequence, drafts);
        }
    }

    private static async Task ReAdjudicateClaimsAsync(EntitlementCoverageChanged message, SerialisationDbContext db,
        ClaimUnitFacts facts, ClaimLineDecider decider, IItemQueryPort items, ICampaignCoverPort campaigns, TenantId tenantId,
        List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        var claims = await db.Claims.Where(c => c.TenantId == tenantId && !ClosedClaimStatuses.Contains(c.Status)
            && (c.UnitId == message.Header.UnitId || db.ClaimLines.Any(l => l.TenantId == tenantId && l.ClaimId == c.ClaimId
                && l.UnitId == message.Header.UnitId))).ToListAsync(cancellationToken);
        foreach (var claim in claims)
        {
            var root = await facts.ReadAsync(claim.UnitId, cancellationToken);
            var item = await items.GetAsync(root.ItemId, cancellationToken);
            var namedCampaign = claim.CampaignId is { } campaignId
                ? await campaigns.FindAsync(campaignId, claim.UnitId, claim.ClaimDate, cancellationToken) : null;
            var campaign = namedCampaign is { Selected: true } && namedCampaign.CoversOn(claim.ClaimDate) ? namedCampaign : null;
            var lines = await db.ClaimLines.Where(l => l.TenantId == tenantId && l.ClaimId == claim.ClaimId)
                .OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
            foreach (var line in lines)
            {
                if (await ReplayedAsync(db, tenantId, message.Header.EventId, CoverageWorkType.Claim, claim.ClaimId, line.ClaimLineId, cancellationToken)) continue;
                var before = Snapshot(line);
                var outcome = await decider.DecideAsync(claim, root, item, line, campaign, cancellationToken);
                ClaimLineDecider.Apply(line, outcome);
                var after = Snapshot(line);
                line.CoverageDecisionRef = AddDecision(message, db, tenantId, CoverageWorkType.Claim, claim.ClaimId,
                    line.ClaimLineId, before, after, line.Sequence, drafts);
            }
            UpdateClaimSummary(claim, lines);
        }
    }

    private static async Task ReAdjudicateServiceAsync(EntitlementCoverageChanged message, SerialisationDbContext db,
        ClaimUnitFacts facts, ClaimLineDecider decider, IItemQueryPort items, IServiceOrderQueryPort serviceOrders,
        TenantId tenantId, List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        foreach (var work in await serviceOrders.OpenForUnitAsync(message.Header.UnitId, cancellationToken))
        {
            if (await ReplayedAsync(db, tenantId, message.Header.EventId, CoverageWorkType.ServiceOrder,
                work.ServiceOrderId, work.ServiceOrderLineId, cancellationToken)) continue;
            var root = await facts.ReadAsync(work.UnitId, cancellationToken);
            var item = await items.GetAsync(root.ItemId, cancellationToken);
            var claim = new Claim
            {
                TenantId = tenantId, ClaimId = work.ServiceOrderId, ClaimNumber = "", UnitId = work.UnitId,
                ClaimDate = work.CoverageDate, ClaimantPartyRef = work.CustomerPartyRef,
                SettlementPartyRef = work.CustomerPartyRef, ServiceEntityId = message.Header.LegalEntityId,
                ObligorEntityId = work.ObligorEntityId, ServiceCountry = work.ServiceCountry, Currency = "",
                CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Repair,
                Status = ClaimStatus.Adjudicating, SubmittedAt = message.Header.EventTime, SubmittedBy = message.Header.ActorRef,
            };
            var line = new ClaimLine
            {
                TenantId = tenantId, ClaimLineId = work.ServiceOrderLineId ?? work.ServiceOrderId, ClaimId = work.ServiceOrderId,
                UnitId = work.UnitId, Sequence = 1, Kind = work.LineKind, CoverageFamily = work.CoverageFamily,
                RequestedAmount = work.RequestedAmount, CoveragePct = work.CurrentDecision.CoveragePct,
                ApprovedAmount = work.CurrentDecision.ApprovedAmount, CoverageSource = work.CurrentDecision.CoverageSource,
                EntitlementId = work.CurrentDecision.EntitlementId, PolicyVersionId = work.CurrentDecision.PolicyVersionId,
                DenialReason = work.CurrentDecision.DenialReason,
                Measurements = JsonSerializer.Serialize(work.Measurements, LedgerJson.Options),
            };
            var outcome = await decider.DecideAsync(claim, root, item, line, null, cancellationToken);
            ClaimLineDecider.Apply(line, outcome);
            var after = Snapshot(line);
            var decisionId = AddDecision(message, db, tenantId, CoverageWorkType.ServiceOrder, work.ServiceOrderId,
                work.ServiceOrderLineId, work.CurrentDecision, after, null, drafts);
            await serviceOrders.UpdateCoverageDecisionReferenceAsync(work.ServiceOrderId, work.ServiceOrderLineId,
                decisionId, after, cancellationToken);
        }
    }

    private static Guid AddDecision(EntitlementCoverageChanged message, SerialisationDbContext db, TenantId tenantId,
        string workType, Guid workId, Guid? workLineId, CoverageDecisionSnapshot before, CoverageDecisionSnapshot after,
        int? documentLine, List<UnitEventDraft> drafts)
    {
        var decisionId = TimeOrderedId.New(message.Header.EventTime);
        var eventId = TimeOrderedId.New(message.Header.EventTime);
        db.CoverageDecisions.Add(new CoverageDecision
        {
            TenantId = tenantId, CoverageDecisionId = decisionId, UnitId = message.Header.UnitId,
            Reason = CoverageDecisionReason.EntitlementChanged, WorkType = workType, WorkId = workId, WorkLineId = workLineId,
            WorkLineKey = workLineId ?? workId,
            BeforeDecision = JsonSerializer.Serialize(before, LedgerJson.Options), AfterDecision = JsonSerializer.Serialize(after, LedgerJson.Options),
            SourceEventId = message.Header.EventId, EventId = eventId, DecidedAt = message.Header.EventTime, ActorRef = message.Header.ActorRef,
        });
        var document = new DocumentRef(workType, workId, documentLine);
        drafts.Add(new UnitEventDraft
        {
            UnitId = message.Header.UnitId, EventTime = message.Header.EventTime, EventType = CoverageEventType.DecisionChanged,
            PresetEventId = eventId, LegalEntityId = message.Header.LegalEntityId, ActorRef = message.Header.ActorRef,
            DocumentType = document.Type, DocumentId = document.Id, DocumentLine = document.Line,
            IdempotencyKey = IdempotencyKey.Derive(message.Header.EventId.ToString("N"), $"coverage:{workType}:{workId:N}:{workLineId:N}"),
            Attributes = JsonObjectText.Of(new { coverage_decision_id = decisionId, reason = CoverageDecisionReason.EntitlementChanged,
                work_type = workType, work_id = workId, work_line_id = workLineId, before, after }),
        });
        return decisionId;
    }

    private static Task<bool> ReplayedAsync(SerialisationDbContext db, TenantId tenantId, Guid sourceEventId,
        string workType, Guid workId, Guid? workLineId, CancellationToken cancellationToken) =>
        db.CoverageDecisions.AsNoTracking().AnyAsync(d => d.TenantId == tenantId && d.SourceEventId == sourceEventId
            && d.WorkType == workType && d.WorkId == workId && d.WorkLineKey == (workLineId ?? workId), cancellationToken);

    private static CoverageDecisionSnapshot Snapshot(ClaimLine line) => new(line.CoveragePct, line.ApprovedAmount,
        line.CoverageSource, line.EntitlementId, line.PolicyVersionId, line.DenialReason, line.ChainLength);

    private static void UpdateClaimSummary(Claim claim, IReadOnlyList<ClaimLine> lines)
    {
        var coveredSources = lines.Where(l => l.ApprovedAmount > 0).Select(l => l.CoverageSource).Distinct(StringComparer.Ordinal).ToList();
        if (coveredSources.Count > 1)
            throw new CommandRefusedException("mixed_coverage_sources", "Re-adjudication would leave one claim against more than one coverage source.", "split the claim before changing its entitlement");
        claim.CoverageSource = coveredSources.SingleOrDefault() ?? ClaimCoverageSource.Chargeable;
        claim.DenialReason = lines.All(l => l.ApprovedAmount == 0)
            ? lines.Select(l => l.DenialReason).FirstOrDefault(r => r is not null) ?? "no_applicable_coverage"
            : lines.Any(l => l.ApprovedAmount < l.RequestedAmount) ? "partially_uncovered" : null;
        claim.ChainLength = lines.Max(l => l.ChainLength);
        claim.EntitlementIds = JsonSerializer.Serialize(lines.Select(l => l.EntitlementId).OfType<Guid>().Distinct().Order().ToList(), LedgerJson.Options);
    }
}
