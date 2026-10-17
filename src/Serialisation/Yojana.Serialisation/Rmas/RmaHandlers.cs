using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Rmas;

public static class RequestRmaHandler
{
    public static async Task<RequestRmaResult> Handle(RequestRma command, SerialisationDbContext db, UnitResolver units,
        ISalesDocumentQueryPort sales, RmaSaleFactsResolver saleFacts, IUnitEventWriter writer, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == command.CommandId, cancellationToken) is { } replay)
        {
            var replayLines = await db.RmaLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.RmaId == replay.RmaId).OrderBy(l => l.Sequence).Select(l => l.RmaLineId).ToListAsync(cancellationToken);
            return new(replay.RmaId, replay.Stage, replayLines, replay.LastEventId, true);
        }

        if (command.Lines.Count == 0) throw new CommandRefusedException("rma_lines_required", "An RMA needs at least one line.");
        if (command.RequestDate > DateOnly.FromDateTime(command.EventTime)) throw new CommandRefusedException("rma_request_date_future", "The RMA request date cannot be after its business time.");
        var resolved = new List<(RmaRequestLineInput Input, Guid UnitId)>();
        foreach (var input in command.Lines)
        {
            var choices = (input.UnitId is not null ? 1 : 0) + (input.Identifier is not null ? 1 : 0) + (input.ProofOfPurchase is not null ? 1 : 0);
            if (choices != 1) throw new CommandRefusedException("rma_identity_choice", "Each RMA line identifies the unit by exactly one of unit id, serial, or proof-of-purchase document.");
            if (!ClaimLineKind.All.Contains(input.ClaimLineKind) || string.IsNullOrWhiteSpace(input.CoverageFamily) || input.RequestedAmount < 0)
                throw new CommandRefusedException("rma_coverage_input_invalid", "Each RMA line needs a known claim-line kind, coverage family and non-negative requested amount.");

            Guid? unitId = input.UnitId;
            if (input.Identifier is { } identifier) unitId = (await units.ResolveAsync(identifier, null, cancellationToken))?.UnitId;
            if (input.ProofOfPurchase is { } proof)
            {
                var shipment = await sales.GetShipmentAsync(proof, cancellationToken)
                    ?? throw new CommandRefusedException("proof_of_purchase_unknown", $"Sales document {proof.Type} {proof.Id:N} is unavailable.");
                var lines = input.ProofLine is { } lineNo ? shipment.Lines.Where(l => l.LineNo == lineNo).ToList() : shipment.Lines.ToList();
                var serials = lines.SelectMany(l => l.Serials.Select(s => (Line: l, Serial: s))).ToList();
                if (serials.Count != 1) throw new CommandRefusedException("proof_of_purchase_not_singular", "A proof-of-purchase RMA line must resolve to exactly one serial.", "supply the document line when the document contains several serials");
                unitId = (await units.ResolveAsync(serials[0].Serial, serials[0].Line.ItemId, cancellationToken))?.UnitId;
            }
            if (unitId is null || !await db.Units.AsNoTracking().AnyAsync(u => u.TenantId == tenantId && u.UnitId == unitId, cancellationToken))
                throw new CommandRefusedException("rma_unit_unknown", "The RMA identity does not resolve to a recorded unit.", "submit the no-record proof through the claim reconstruction path first");
            resolved.Add((input, unitId.Value));
        }
        if (resolved.Select(x => x.UnitId).Distinct().Count() != resolved.Count)
            throw new CommandRefusedException("rma_unit_duplicate", "A unit may appear only once in one RMA.");

        var rmaId = TimeOrderedId.New(command.EventTime);
        var eventId = TimeOrderedId.New(command.EventTime);
        var rma = new Rma
        {
            TenantId = tenantId, RmaId = rmaId, CommandId = command.CommandId, Stage = RmaStage.Requested,
            RequestDate = command.RequestDate, ReturningPartyRef = command.ReturningPartyRef, SettlementPartyRef = command.SettlementPartyRef,
            ServiceEntityId = command.ServiceEntityId, ObligorEntityId = command.ObligorEntityId, ServiceCountry = command.ServiceCountry.Trim().ToUpperInvariant(),
            Currency = command.Currency.Trim().ToUpperInvariant(), RequestedAt = command.EventTime, RequestedBy = command.ActorRef,
            LastActorRef = command.ActorRef, LastEventId = eventId,
        };
        db.Rmas.Add(rma);
        var linesToWrite = new List<RmaLine>();
        var drafts = new List<UnitEventDraft>();
        foreach (var ((input, unitId), index) in resolved.Select((value, index) => (value, index)))
        {
            var facts = await saleFacts.ResolveAsync(unitId, cancellationToken);
            var lineId = TimeOrderedId.New(command.EventTime);
            var line = new RmaLine
            {
                TenantId = tenantId, RmaLineId = lineId, RmaId = rmaId, Sequence = index + 1, RequestedUnitId = unitId,
                OwnershipEpisodeId = facts.OwnershipEpisodeId, OriginalSaleDocumentType = facts.OriginalSaleRef.Type,
                OriginalSaleDocumentId = facts.OriginalSaleRef.Id, OriginalSaleDocumentLine = facts.OriginalSaleRef.Line,
                ParentUnitId = facts.ParentUnitId, PriceAllocation = facts.PriceAllocation, CoverageFamily = input.CoverageFamily,
                ClaimLineKind = input.ClaimLineKind, RequestedAmount = input.RequestedAmount, Description = input.Description,
            };
            linesToWrite.Add(line);
            drafts.Add(new UnitEventDraft
            {
                UnitId = unitId, EventTime = command.EventTime, EventType = RmaEventType.Requested,
                PresetEventId = index == 0 ? eventId : TimeOrderedId.New(command.EventTime), LegalEntityId = command.ServiceEntityId,
                ActorRef = command.ActorRef, IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unitId, RmaEventType.Requested, command.EventTime),
                Attributes = JsonObjectText.Of(new { rma_id = rmaId, rma_line_id = lineId, request_date = command.RequestDate, ownership_episode_id = facts.OwnershipEpisodeId, original_sale_ref = facts.OriginalSaleRef }),
            });
        }
        db.RmaLines.AddRange(linesToWrite);
        await writer.AppendAsync(drafts, cancellationToken);
        return new(rmaId, rma.Stage, linesToWrite.Select(l => l.RmaLineId).ToList(), eventId, false);
    }
}

public static class AuthoriseRmaHandler
{
    public static async Task<AuthoriseRmaResult> Handle(AuthoriseRma command, SerialisationDbContext db, ClaimUnitFacts facts,
        ClaimLineDecider decider, IItemQueryPort items, DocumentNumbers numbers, IUnitEventWriter writer, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RmaId == command.RmaId, cancellationToken)
            ?? throw new CommandRefusedException("rma_unknown", $"RMA {command.RmaId} does not exist.");
        var lines = await db.RmaLines.Where(l => l.TenantId == tenantId && l.RmaId == rma.RmaId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        if (rma.Stage != RmaStage.Requested)
        {
            var replayed = await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.IdempotencyKey == IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, lines[0].RequestedUnitId, RmaEventType.Authorised, command.EventTime) && e.EventTime == command.EventTime, cancellationToken);
            if (!replayed) RmaStateMachine.Transition(rma, RmaStage.Authorised);
            else return Result(rma, lines, rma.LastEventId, true);
        }
        else RmaStateMachine.Transition(rma, RmaStage.Authorised);

        rma.RmaNumber ??= await numbers.NextAsync(DocumentNumbers.Rma, command.EventTime, cancellationToken);
        var eventId = TimeOrderedId.New(command.EventTime);
        var drafts = new List<UnitEventDraft>();
        foreach (var (line, index) in lines.Select((value, index) => (value, index)))
        {
            var root = await facts.ReadAsync(line.RequestedUnitId, cancellationToken);
            var item = await items.GetAsync(root.ItemId, cancellationToken);
            var claim = new Claim
            {
                TenantId = tenantId, ClaimId = rma.RmaId, ClaimNumber = rma.RmaNumber, CommandId = command.CommandId, UnitId = line.RequestedUnitId,
                ClaimDate = rma.RequestDate, ClaimantPartyRef = rma.ReturningPartyRef, SettlementPartyRef = rma.SettlementPartyRef,
                ServiceEntityId = rma.ServiceEntityId, ObligorEntityId = rma.ObligorEntityId, ServiceCountry = rma.ServiceCountry, Currency = rma.Currency,
                CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Replacement, Status = ClaimStatus.Adjudicating,
                SubmittedAt = rma.RequestedAt, SubmittedBy = rma.RequestedBy,
            };
            var claimLine = new ClaimLine
            {
                TenantId = tenantId, ClaimLineId = line.RmaLineId, ClaimId = rma.RmaId, UnitId = line.RequestedUnitId, Sequence = line.Sequence,
                Kind = line.ClaimLineKind, CoverageFamily = line.CoverageFamily, RequestedAmount = line.RequestedAmount,
                CoverageSource = ClaimCoverageSource.Chargeable, Measurements = "{}",
            };
            var outcome = await decider.DecideAsync(claim, root, item, claimLine, null, cancellationToken);
            line.AuthorisedUnitId = line.RequestedUnitId;
            line.CoveragePct = outcome.CoveragePct;
            line.ApprovedAmount = outcome.ApprovedAmount;
            line.CoverageSource = outcome.CoverageSource;
            line.EntitlementId = outcome.EntitlementId;
            line.PolicyVersionId = outcome.PolicyVersionId;
            line.DenialReason = outcome.DenialReason;
            var original = new DocumentRef(line.OriginalSaleDocumentType, line.OriginalSaleDocumentId, line.OriginalSaleDocumentLine);
            drafts.Add(new UnitEventDraft
            {
                UnitId = line.RequestedUnitId, EventTime = command.EventTime, EventType = RmaEventType.Authorised,
                PresetEventId = index == 0 ? eventId : TimeOrderedId.New(command.EventTime), LegalEntityId = rma.ServiceEntityId,
                ActorRef = command.ActorRef, DocumentType = original.Type, DocumentId = original.Id, DocumentLine = original.Line,
                IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, line.RequestedUnitId, RmaEventType.Authorised, command.EventTime),
                Attributes = JsonObjectText.Of(new { rma_id = rma.RmaId, rma_line_id = line.RmaLineId, rma_number = rma.RmaNumber, authorised_unit_id = line.AuthorisedUnitId, ownership_episode_id = line.OwnershipEpisodeId, original_sale_ref = original, coverage_pct = line.CoveragePct, coverage_source = line.CoverageSource, entitlement_id = line.EntitlementId }),
            });
        }
        rma.AuthorisedAt = command.EventTime;
        rma.LastActorRef = command.ActorRef;
        rma.LastEventId = eventId;
        await writer.AppendAsync(drafts, cancellationToken);
        return Result(rma, lines, eventId, false);
    }

    private static AuthoriseRmaResult Result(Rma rma, IReadOnlyList<RmaLine> lines, Guid eventId, bool replayed) =>
        new(rma.RmaId, rma.RmaNumber!, rma.Stage, lines.Select(l => new RmaLineCoverageDecision(l.RmaLineId, l.AuthorisedUnitId!.Value,
            l.CoveragePct ?? 0, l.ApprovedAmount ?? 0, l.CoverageSource ?? ClaimCoverageSource.Chargeable, l.EntitlementId, l.PolicyVersionId, l.DenialReason)).ToList(), eventId, replayed);
}

public static class ReceiveReturnHandler
{
    public static async Task<ReceiveReturnResult> Handle(ReceiveReturn command, SerialisationDbContext db, UnitResolver units,
        EpisodeService episodes, PostingPeriods periods, IStockUnitEventPort stock, IUnitEventWriter writer,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RmaId == command.RmaId, cancellationToken)
            ?? throw new CommandRefusedException("rma_unknown", $"RMA {command.RmaId} does not exist.");
        var lines = await db.RmaLines.Where(l => l.TenantId == tenantId && l.RmaId == rma.RmaId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        if (command.Lines.Count != lines.Count || command.Lines.Select(x => x.RmaLineId).Distinct().Count() != lines.Count || command.Lines.Any(x => lines.All(l => l.RmaLineId != x.RmaLineId)))
            throw new CommandRefusedException("rma_receipt_lines_incomplete", "Receipt must account for every RMA line exactly once.");
        var replayReceipts = await db.ReturnReceipts.AsNoTracking().Where(x => x.TenantId == tenantId && x.CommandId == command.CommandId && x.RmaId == rma.RmaId).OrderBy(x => x.RmaLineId).ToListAsync(cancellationToken);
        if (replayReceipts.Count == lines.Count) return Result(rma, replayReceipts, rma.LastEventId, true);
        if (command.DispatchedAt > command.ReceivedAt)
            throw new CommandRefusedException("return_time_invalid", "A return cannot be received before it was dispatched.");
        RmaStateMachine.Transition(rma, RmaStage.InTransit);
        rma.InTransitAt = command.DispatchedAt;
        RmaStateMachine.Transition(rma, RmaStage.Received);
        rma.ReceivedAt = command.ReceivedAt;
        var postingPeriod = await periods.ResolveAsync(command.ReceivedAt, cancellationToken);
        var resolved = new Dictionary<Guid, Guid?>();
        foreach (var input in command.Lines)
        {
            if (!ConditionGrades.IsKnown(input.ConditionGrade)) throw new CommandRefusedException("condition_grade_invalid", $"'{input.ConditionGrade}' is not a condition grade.");
            if (!ReturnOutcome.All.Contains(input.Outcome)) throw new CommandRefusedException("return_outcome_invalid", $"'{input.Outcome}' is not a return outcome.");
            resolved[input.RmaLineId] = input.ReceivedIdentifier is null ? null : (await units.ResolveAsync(input.ReceivedIdentifier, null, cancellationToken))?.UnitId;
        }
        await episodes.PreloadOpenAsync(resolved.Values.OfType<Guid>().Distinct().ToList(), cancellationToken);
        var drafts = new List<UnitEventDraft>();
        var receipts = new List<ReturnReceipt>();
        foreach (var input in command.Lines)
        {
            var line = lines.Single(l => l.RmaLineId == input.RmaLineId);
            var authorised = line.AuthorisedUnitId ?? throw new CommandRefusedException("rma_line_not_authorised", $"RMA line {line.RmaLineId} is not authorised.");
            var receivedUnit = resolved[input.RmaLineId];
            var provisional = receivedUnit is null ? TimeOrderedId.New(command.ReceivedAt) : (Guid?)null;
            var outcome = input.Outcome == ReturnOutcome.Accepted && receivedUnit is { } received && received != authorised ? ReturnOutcome.Mismatch : input.Outcome;
            var receiptId = TimeOrderedId.New(command.ReceivedAt);
            var eventId = TimeOrderedId.New(command.ReceivedAt);
            if (receivedUnit is { } unitId)
            {
                var current = await episodes.OpenAsync(unitId, EpisodeKind.Custody, cancellationToken);
                if (current?.LocationKind != LocationKind.Returning
                    && CustodyTransitions.CustodyAllowed(current?.LocationKind, LocationKind.Returning))
                {
                    var returning = await episodes.PrepareCustodyMoveAsync(unitId, new CustodyTarget(LocationKind.Returning, PartyRef: rma.ReturningPartyRef, PartyType: PartyType.Customer, Reason: EpisodeReason.Return, Source: "rma"),
                        UnitEventType.CustodyChanged, IdempotencyKey.Derive(command.CommandId.ToString("N"), $"returning:{line.RmaLineId:N}"), command.DispatchedAt,
                        command.ActorRef, rma.ServiceEntityId, null, new Dictionary<string, object?> { ["rma_id"] = rma.RmaId, ["rma_line_id"] = line.RmaLineId }, null, true, cancellationToken);
                    drafts.Add(returning.Draft);
                }
                if (!CustodyTransitions.CustodyAllowed(
                        (await episodes.OpenAsync(unitId, EpisodeKind.Custody, cancellationToken))?.LocationKind,
                        LocationKind.Quarantine))
                    throw new CommandRefusedException("return_custody_not_accepted", $"Unit {unitId} cannot enter return quarantine from its current custody.");
                var quarantine = await episodes.PrepareCustodyMoveAsync(unitId, new CustodyTarget(LocationKind.Quarantine, command.ReceivingLocationId, Reason: EpisodeReason.Return, Source: "rma"),
                    UnitEventType.CustodyChanged, IdempotencyKey.Derive(command.CommandId.ToString("N"), $"quarantine:{line.RmaLineId:N}"), command.ReceivedAt,
                    command.ActorRef, rma.ServiceEntityId, null, new Dictionary<string, object?> { ["rma_id"] = rma.RmaId, ["return_receipt_id"] = receiptId }, new CurrentStateDelta { ConditionGrade = input.ConditionGrade }, true, cancellationToken);
                drafts.Add(quarantine.Draft with { PostingPeriod = postingPeriod });
                await stock.RecordReturnForPosting(new StockReturnPosting(command.CommandId, receiptId, unitId, rma.RmaId, line.RmaLineId, command.ReceivingLocationId, input.ConditionGrade, input.ValuationRule, command.ReceivedAt, postingPeriod), cancellationToken);
            }
            var receipt = new ReturnReceipt
            {
                TenantId = tenantId, ReturnReceiptId = receiptId, CommandId = command.CommandId, RmaId = rma.RmaId,
                RmaLineId = line.RmaLineId, AuthorisedUnitId = authorised, ReceivedUnitId = receivedUnit, ProvisionalUnitId = provisional,
                Outcome = outcome, ConditionGrade = input.ConditionGrade, ValuationRule = input.ValuationRule, ExceptionNote = input.ExceptionNote,
                ReceivingLocationId = command.ReceivingLocationId, BusinessTime = command.ReceivedAt, PostingPeriod = postingPeriod,
                RecordedAt = clock.UtcNow, ReceivedBy = command.ActorRef, EventId = eventId,
            };
            receipts.Add(receipt);
            line.ReceivedUnitId = receivedUnit;
            line.ProvisionalUnitId = provisional;
            line.ReceiptOutcome = outcome;
            line.ConditionGrade = input.ConditionGrade;
            line.ValuationRule = input.ValuationRule;
            drafts.Add(new UnitEventDraft
            {
                UnitId = receivedUnit ?? authorised, EventTime = command.ReceivedAt, EventType = RmaEventType.Received, PresetEventId = eventId,
                LegalEntityId = rma.ServiceEntityId, ActorRef = command.ActorRef, PostingPeriod = postingPeriod,
                IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, receivedUnit ?? authorised, RmaEventType.Received, command.ReceivedAt),
                Attributes = JsonObjectText.Of(new { rma_id = rma.RmaId, rma_line_id = line.RmaLineId, return_receipt_id = receiptId, authorised_unit_id = authorised, received_unit_id = receivedUnit, provisional_unit_id = provisional, outcome, condition_grade = input.ConditionGrade, valuation_rule = input.ValuationRule, posting_period = postingPeriod }),
            });
        }
        db.ReturnReceipts.AddRange(receipts);
        rma.LastActorRef = command.ActorRef;
        rma.LastEventId = receipts[0].EventId;
        await writer.AppendAsync(drafts, cancellationToken);
        return Result(rma, receipts, receipts[0].EventId, false);
    }

    private static ReceiveReturnResult Result(Rma rma, IReadOnlyList<ReturnReceipt> receipts, Guid eventId, bool replayed) =>
        new(rma.RmaId, rma.Stage, receipts.Select(x => new ReturnReceiptResultLine(x.RmaLineId, x.ReturnReceiptId, x.ReceivedUnitId, x.ProvisionalUnitId,
            x.Outcome, x.Outcome != ReturnOutcome.Accepted || x.ProvisionalUnitId is not null)).ToList(), eventId, replayed);
}

public static class DispositionRmaHandler
{
    public static async Task<DispositionRmaResult> Handle(DispositionRma command, SerialisationDbContext db, EpisodeService episodes,
        EntitlementLedger entitlementLedger, CorrectionLedger corrections, IReplacementIntentPort replacements,
        IRefurbishmentOpener refurbishments, IUnitEventWriter writer,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RmaId == command.RmaId, cancellationToken)
            ?? throw new CommandRefusedException("rma_unknown", $"RMA {command.RmaId} does not exist.");
        var lines = await db.RmaLines.Where(l => l.TenantId == tenantId && l.RmaId == rma.RmaId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        if (rma.Stage == RmaStage.Closed && lines.All(l => l.Disposition is not null))
        {
            var replayUnitId = lines[0].ReceivedUnitId;
            var replayed = replayUnitId is { } unitId && await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId
                && e.EventType == RmaEventType.Dispositioned && e.EventTime == command.EventTime
                && e.IdempotencyKey == IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unitId, RmaEventType.Dispositioned, command.EventTime), cancellationToken);
            if (replayed) return Result(rma, lines, rma.LastEventId, true);
            RmaStateMachine.Transition(rma, RmaStage.Dispositioned);
        }
        if (command.Lines.Count != lines.Count || command.Lines.Select(x => x.RmaLineId).Distinct().Count() != lines.Count || command.Lines.Any(x => lines.All(l => l.RmaLineId != x.RmaLineId)))
            throw new CommandRefusedException("rma_disposition_lines_incomplete", "Disposition must account for every RMA line exactly once.");
        if (command.Lines.Any(x => !RmaDisposition.All.Contains(x.Disposition))) throw new CommandRefusedException("rma_disposition_invalid", "Every line needs a known disposition.");
        if (lines.Any(l => l.ReceiptOutcome is ReturnOutcome.Mismatch or ReturnOutcome.Unauthorised or ReturnOutcome.Counterfeit))
            throw new CommandRefusedException("rma_receipt_exception_open", "Mismatch, unauthorised and counterfeit receipts cannot be dispositioned silently.", "resolve the receipt exception first");

        RmaStateMachine.Transition(rma, RmaStage.Dispositioned);
        rma.DispositionedAt = command.EventTime;
        var knownUnits = lines.Select(l => l.ReceivedUnitId).OfType<Guid>().Distinct().ToList();
        foreach (var line in lines.Where(l => l.ParentUnitId is not null))
        {
            if (line.PriceAllocation is null)
                throw new CommandRefusedException("kit_price_allocation_required", $"Child return {line.RmaLineId} has no sale-time price allocation.", "correct the immutable sales document allocation before disposition");
            var link = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ParentUnitId == line.ParentUnitId
                && l.ChildUnitId == line.ReceivedUnitId && l.RemovedAt == null).Select(l => (Guid?)l.LinkId).SingleOrDefaultAsync(cancellationToken);
            if (link is { } linkId)
            {
                await RecordDisassemblyHandler.Handle(new RecordDisassembly(command.CommandId, line.ParentUnitId!.Value, [linkId], command.EventTime,
                    command.ActorRef, rma.ServiceEntityId, "partial_kit_return", new DocumentRef("rma", rma.RmaId, line.Sequence)), db, writer, tenant, cancellationToken);
            }
        }
        // RecordDisassembly and this handler share Wolverine's ambient transaction. Expose the closed link to the
        // custody invariant query before the child enters stock; a later refusal still rolls both changes back.
        await db.SaveChangesAsync(cancellationToken);
        await episodes.PreloadOpenAsync(knownUnits, cancellationToken);
        var receiptTimes = await db.ReturnReceipts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.RmaId == rma.RmaId)
            .ToDictionaryAsync(x => x.RmaLineId, x => x.BusinessTime, cancellationToken);
        var drafts = new List<UnitEventDraft>();
        foreach (var input in command.Lines)
        {
            var line = lines.Single(l => l.RmaLineId == input.RmaLineId);
            var unitId = line.ReceivedUnitId ?? throw new CommandRefusedException("received_unit_required", $"Line {line.RmaLineId} has only a provisional identity and cannot be dispositioned.", "resolve the provisional identity first");
            string? closedReason = null;
            Guid? replacementIntentId = null;
            var document = new DocumentRef("rma", rma.RmaId, line.Sequence);
            if (input.Disposition is RmaDisposition.Credit or RmaDisposition.Refund)
            {
                closedReason = await CloseEntitlementAsync(line, EntitlementClosedReason.Refunded, command, entitlementLedger, corrections, clock, document, cancellationToken);
            }
            else if (input.Disposition == RmaDisposition.ReturnToStock && line.EntitlementId is { } entitlementId)
            {
                var entitlement = await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId && e.EntitlementId == entitlementId, cancellationToken);
                if (entitlement?.ParentEntitlementId is not null)
                    closedReason = await CloseEntitlementAsync(line, EntitlementClosedReason.ReturnedToStock, command, entitlementLedger, corrections, clock, document, cancellationToken);
            }
            else if (input.Disposition == RmaDisposition.Replace)
            {
                replacementIntentId = await replacements.RecordAsync(new ReplacementIntent(command.CommandId, rma.RmaId, line.RmaLineId, unitId, command.EffectiveDate, command.EventTime), cancellationToken);
            }

            if (ReturnsOwnershipToUs(input.Disposition))
            {
                if (!receiptTimes.TryGetValue(line.RmaLineId, out var receivedAt))
                    throw new CommandRefusedException("return_receipt_required", $"Line {line.RmaLineId} has no return receipt business time.");
                await PrepareReturnedOwnershipAsync(unitId, line, rma, command, document, receivedAt, episodes, drafts, cancellationToken);
            }

            Guid? refurbishmentWorkOrderId = null;
            if (input.Disposition == RmaDisposition.Refurbish)
            {
                // RMA knows only this opening contract. The refurbishment module owns its tables, lifecycle episode and
                // custody move; retaining this seam prevents either aggregate from depending on the other's internals.
                var opened = await refurbishments.OpenAsync(new RefurbishmentOpenRequest(command.CommandId, unitId,
                    line.RmaLineId, document, command.EventTime, command.ActorRef, rma.ServiceEntityId), cancellationToken);
                refurbishmentWorkOrderId = opened.WorkOrderId;
            }

            var target = input.Disposition switch
            {
                RmaDisposition.ReturnToStock or RmaDisposition.RestockAsNew or RmaDisposition.Credit or RmaDisposition.Refund => LocationKind.Warehouse,
                RmaDisposition.RepairAndReturn => LocationKind.InRepair,
                RmaDisposition.ReturnToVendor => LocationKind.Vendor,
                RmaDisposition.Scrap => LocationKind.Scrapped,
                _ => null,
            };
            if (target is not null)
            {
                if (target == LocationKind.Vendor && input.VendorPartyRef is null) throw new CommandRefusedException("vendor_required", "Return-to-vendor disposition needs the vendor party.");
                var move = await episodes.PrepareCustodyMoveAsync(unitId,
                    new CustodyTarget(target, PartyRef: input.VendorPartyRef, PartyType: target == LocationKind.Vendor ? PartyType.Vendor : null, Reason: input.Disposition, Source: "rma"),
                    UnitEventType.CustodyChanged, IdempotencyKey.Derive(command.CommandId.ToString("N"), $"custody:{line.RmaLineId:N}"), command.EventTime,
                    command.ActorRef, rma.ServiceEntityId, document, new Dictionary<string, object?> { ["rma_id"] = rma.RmaId, ["rma_line_id"] = line.RmaLineId },
                    target == LocationKind.Scrapped ? new CurrentStateDelta { LifecycleStatus = LifecycleStatus.Scrapped, Disposition = UnitDisposition.Destroyed } : null,
                    true, cancellationToken);
                drafts.Add(move.Draft);
            }
            if (line.ParentUnitId is { } parentId)
            {
                line.ParentIncomplete = true;
                drafts.Add(new UnitEventDraft
                {
                    UnitId = parentId, EventTime = command.EventTime, EventType = RmaEventType.KitIncomplete, LegalEntityId = rma.ServiceEntityId, ActorRef = command.ActorRef,
                    IdempotencyKey = IdempotencyKey.Derive(command.CommandId.ToString("N"), $"kit:{line.RmaLineId:N}"),
                    Attributes = JsonObjectText.Of(new { rma_id = rma.RmaId, rma_line_id = line.RmaLineId, returned_child_unit_id = unitId, price_allocation = line.PriceAllocation }),
                    Delta = new CurrentStateDelta { Disposition = UnitDisposition.Incomplete },
                });
            }
            line.Disposition = input.Disposition;
            line.ReplacementIntentId = replacementIntentId;
            line.EntitlementClosedReason = closedReason;
            var eventId = TimeOrderedId.New(command.EventTime);
            drafts.Add(new UnitEventDraft
            {
                UnitId = unitId, EventTime = command.EventTime, EventType = RmaEventType.Dispositioned, PresetEventId = eventId,
                LegalEntityId = rma.ServiceEntityId, ActorRef = command.ActorRef, DocumentType = document.Type, DocumentId = document.Id, DocumentLine = document.Line,
                IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unitId, RmaEventType.Dispositioned, command.EventTime),
                Attributes = JsonObjectText.Of(new { rma_id = rma.RmaId, rma_line_id = line.RmaLineId, unit_id = unitId, disposition = input.Disposition, entitlement_closed_reason = closedReason, replacement_intent_id = replacementIntentId, refurbishment_work_order_id = refurbishmentWorkOrderId }),
            });
        }
        RmaStateMachine.Transition(rma, RmaStage.Closed);
        rma.ClosedAt = command.EventTime;
        rma.LastActorRef = command.ActorRef;
        rma.LastEventId = drafts.Last(d => d.EventType == RmaEventType.Dispositioned).PresetEventId!.Value;
        await writer.AppendAsync(drafts, cancellationToken);
        return Result(rma, lines, rma.LastEventId, false);
    }

    private static bool ReturnsOwnershipToUs(string disposition) => disposition is
        RmaDisposition.Replace or RmaDisposition.Credit or RmaDisposition.Refund or RmaDisposition.ReturnToStock
        or RmaDisposition.RestockAsNew or RmaDisposition.Refurbish or RmaDisposition.Scrap or RmaDisposition.ReturnToVendor;

    private static async Task PrepareReturnedOwnershipAsync(Guid unitId, RmaLine line, Rma rma, DispositionRma command,
        DocumentRef document, DateTime receivedAt, EpisodeService episodes, List<UnitEventDraft> drafts,
        CancellationToken cancellationToken)
    {
        var current = await episodes.OpenAsync(unitId, EpisodeKind.Ownership, cancellationToken);
        if (current?.PartyType == PartyType.Us) return;

        // Return-to-vendor first returns customer ownership to us at receipt. The later vendor-facing move belongs
        // to P5-T08; moving ownership directly to the vendor here would skip the legally relevant customer return.
        var ownership = await episodes.PrepareOwnershipOpenAsync(unitId,
            new OwnershipTarget(rma.ServiceEntityId, PartyType.Us, EpisodeReason.Return, Source: "rma"),
            IdempotencyKey.Derive(command.CommandId.ToString("N"), $"ownership:{line.RmaLineId:N}"), receivedAt,
            command.ActorRef, rma.ServiceEntityId, document, EpisodeReason.Return, cancellationToken);
        if (ownership.CloseDraft is not null) drafts.Add(ownership.CloseDraft);
        drafts.Add(ownership.Draft);
    }

    private static async Task<string?> CloseEntitlementAsync(RmaLine line, string reason, DispositionRma command,
        EntitlementLedger ledger, CorrectionLedger corrections, IClock clock, DocumentRef document, CancellationToken cancellationToken)
    {
        if (line.EntitlementId is not { } entitlementId) return null;
        await CloseEntitlementHandler.Handle(new CloseEntitlement(command.CommandId, entitlementId, reason, command.EffectiveDate,
            command.EventTime, command.ActorRef, document, $"RMA {command.RmaId:N} disposition"), ledger, clock, corrections, cancellationToken);
        return reason;
    }

    private static DispositionRmaResult Result(Rma rma, IReadOnlyList<RmaLine> lines, Guid eventId, bool replayed) =>
        new(rma.RmaId, rma.Stage, lines.Select(l => new RmaDispositionResultLine(l.RmaLineId, l.Disposition!, l.ReplacementIntentId, l.EntitlementClosedReason)).ToList(), eventId, replayed);
}
