using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Corrections;

/// <summary>Pure rules at the reservation/Manufacturing contract boundary.</summary>
public static class ReassignAllocationRules
{
    public const string ProductionOrderDocumentType = "production_order";
    public const string AlreadyCommissioned = "already_commissioned";
    public const string CommissionedUnblockingStep = "remove the pair, or record a crossed pair with SwapIdentifiers";
    public const string QuantityUnblockingStep = "exchange instead or raise the order quantity in Manufacturing";

    public static CommandRefusal? CompatibilityProblem(ProductionOrderAllocation source, ProductionOrderAllocation target)
    {
        if (source.ItemId != target.ItemId || !string.Equals(source.SerialScheme, target.SerialScheme, StringComparison.Ordinal))
            return new("target_order_item_or_scheme_differs", "The two production orders use different items or serial schemes.", "choose production orders with the same item and serial scheme");
        return null;
    }

    public static bool SameDocument(DocumentRef left, DocumentRef right) =>
        left.Id == right.Id && left.Line == right.Line && string.Equals(left.Type, right.Type, StringComparison.Ordinal);
}

// TIGHT COUPLING (P3-T11 effective reservation)
// WHAT: this handler updates SerialAllocationOverride, while SerialRangeService reads that table when it accepts a
// commissioned serial; reassignment takes the scheme's exclusive application lock and commissioning takes its shared
// lock. WHY: a moved serial must commission against its new order without splitting the source range or racing a
// commission. WHAT BREAKS IF SEPARATED: commissioning can put a moved serial on its old order while the override commits
// to the new one. Keep the mirrored comment in SerialRangeService and both locks in SerialAllocationReservationStore.
//
// TIGHT COUPLING (P3-T11 reservation audit)
// WHAT: the override and its SerialAllocationReassignment fact are written in the same local transaction. WHY: no unit
// ledger exists before commissioning, but current allocation and immutable correction history must agree. WHAT BREAKS
// IF SEPARATED: either side can commit alone. Keep this pairing here and publish only through the transactional outbox.
public static class ReassignAllocationHandler
{
    public static async Task<ReassignAllocationResult> Handle(
        ReassignAllocation command,
        SerialisationDbContext db,
        SerialAllocationReservationStore reservations,
        CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings,
        IProductionOrderAllocationPort productionOrders,
        ISerialSchemeSource schemes,
        IMessageBus bus,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var correctionId = command.Correction?.CorrectionId ?? command.CommandId;
        try
        {
            ValidateShape(command);
            if (!command.PreviewOnly && await db.AllocationReassignments.AsNoTracking()
                    .Where(r => r.TenantId == tenant.TenantId && r.CommandId == command.CommandId)
                    .Select(r => r.Scheme).FirstOrDefaultAsync(cancellationToken) is { } replaySchemeCode)
            {
                var replayScheme = schemes.Find(replaySchemeCode)
                    ?? throw new CommandRefusedException("scheme_unknown", $"Serial scheme '{replaySchemeCode}' recorded by the command is no longer defined.", "restore the scheme definition before replaying the command");
                var replayFrom = Expand(command.FromSerials, replayScheme);
                var replayExchange = command.Mode == AllocationReassignmentMode.PairwiseExchange ? Expand(command.ToSerials!, replayScheme) : [];
                ValidateSelections(command, replayFrom, replayExchange);
                return await ReplayAsync(command, correctionId, replayFrom, replayExchange, db, tenant.TenantId, cancellationToken)
                    ?? throw new InvalidOperationException("The replay probe found allocation facts that could not be reloaded.");
            }

            var sourceOrder = await LoadOrderAsync(command.FromProductionOrder, productionOrders, cancellationToken);
            var targetOrder = await LoadOrderAsync(command.ToProductionOrder, productionOrders, cancellationToken);
            if (ReassignAllocationRules.CompatibilityProblem(sourceOrder, targetOrder) is { } compatibility)
                throw Refused(compatibility);
            var scheme = schemes.Find(sourceOrder.SerialScheme)
                ?? throw new CommandRefusedException("scheme_unknown", $"Serial scheme '{sourceOrder.SerialScheme}' is not defined for this tenant.", "define the scheme in settings");
            var from = Expand(command.FromSerials, scheme);
            var exchange = command.Mode == AllocationReassignmentMode.PairwiseExchange ? Expand(command.ToSerials!, scheme) : [];
            ValidateSelections(command, from, exchange);

            var metadata = await corrections.PrepareAsync(CorrectionCommand.ReassignAllocation, command.Correction, command.CommandId,
                command.Reason, command.Note, null, command.EventTime, CorrectionInitiatingRole.ProductionSupervisor, cancellationToken);
            var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "An allocation reassignment names who commits it and the roles they hold.");
            if (!string.Equals(committer.UserRef, command.ActorRef, StringComparison.Ordinal))
                throw new CommandRefusedException("committer_actor_mismatch", "The correction actor must be the person committing it.");
            var approval = CorrectionApprovalRules.Evaluate(await approvalSettings.GetAsync(cancellationToken), CorrectionCommand.ReassignAllocation, new(), committer.Roles);
            var approvalRefusal = approval.Status == CorrectionApprovalStatus.Allowed
                ? null
                : approval.Refusal ?? new CommandRefusal("approval_required", "The allocation reassignment needs approval.");
            if (!command.PreviewOnly && approvalRefusal is not null) throw Refused(approvalRefusal);

            if (!command.PreviewOnly)
            {
                await reservations.LockForReassignmentAsync(scheme.Code, cancellationToken);
                if (await ReplayAsync(command, correctionId, from, exchange, db, tenant.TenantId, cancellationToken) is { } concurrentReplay)
                    return concurrentReplay;
            }
            var plan = await BuildPlanAsync(command, sourceOrder, targetOrder, scheme, from, exchange, reservations, db, tenant.TenantId, cancellationToken);
            await ValidateReversalAsync(metadata, plan, db, tenant.TenantId, cancellationToken);
            var preview = Preview(plan);
            if (command.PreviewOnly) return new(metadata.CorrectionId, null, preview with { Refusal = approvalRefusal }, false);
            if (plan.Any(p => p.Blocked))
                throw new CommandRefusedException(ReassignAllocationRules.AlreadyCommissioned,
                    "At least one selected pair contains a serial that has already been commissioned; nothing was moved.",
                    ReassignAllocationRules.CommissionedUnblockingStep);

            var eventId = TimeOrderedId.New(command.EventTime);
            var recordedTime = clock.UtcNow;
            var published = new List<PublishedAllocationReassignment>();
            foreach (var pair in plan)
            {
                foreach (var move in pair.Moves)
                {
                    reservations.SetEffectiveOrder(move.Reservation, scheme.Code, move.Serial.Ordinal, move.Serial.Value,
                        move.Target, metadata.CorrectionId, recordedTime);
                    var attributes = CorrectionLedger.Attributes(metadata, new
                    {
                        command_id = command.CommandId,
                        pair = pair.Pair,
                        mode = command.Mode,
                        serial = move.Serial.Value,
                        paired_serial = pair.Exchange?.Serial.Value,
                        serial_range_id = move.Reservation.Range.RangeId,
                        scheme = scheme.Code,
                        serial_ordinal = move.Serial.Ordinal,
                        previous_production_order = move.Previous,
                        new_production_order = move.Target,
                    });
                    db.AllocationReassignments.Add(new SerialAllocationReassignment
                    {
                        TenantId = tenant.TenantId,
                        ReassignmentId = TimeOrderedId.New(command.EventTime),
                        CommandId = command.CommandId,
                        CorrectionId = metadata.CorrectionId,
                        EventId = eventId,
                        Pair = pair.Pair,
                        Mode = command.Mode,
                        SerialRangeId = move.Reservation.Range.RangeId,
                        Scheme = scheme.Code,
                        SerialOrdinal = move.Serial.Ordinal,
                        SerialValue = move.Serial.Value,
                        PreviousProductionOrderType = move.Previous.Type,
                        PreviousProductionOrderId = move.Previous.Id,
                        PreviousProductionOrderLine = move.Previous.Line,
                        NewProductionOrderType = move.Target.Type,
                        NewProductionOrderId = move.Target.Id,
                        NewProductionOrderLine = move.Target.Line,
                        EventTime = command.EventTime,
                        RecordedTime = recordedTime,
                        ActorRef = command.ActorRef,
                        LegalEntityId = command.LegalEntityId,
                        Attributes = attributes.Json,
                    });
                    published.Add(new(move.Serial.Value, move.Reservation.Range.RangeId, move.Previous, move.Target));
                }
            }

            corrections.Record(CorrectionCommand.ReassignAllocation, metadata, command.ActorRef, command.EventTime, 0);
            await bus.PublishAsync(new UnitAllocationReassigned(
                new(UnitAllocationReassigned.Schema, UnitAllocationReassigned.Version, eventId, command.EventTime, recordedTime,
                    command.LegalEntityId, command.ActorRef, null),
                new(metadata.CorrectionId, metadata.ReasonCode, metadata.InitiatedVia, metadata.CorrectsCorrectionId,
                    (metadata.Approvers ?? []).Select(a => a.UserRef).ToList()), published),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            return new(metadata.CorrectionId, eventId, preview, false);
        }
        catch (CommandRefusedException ex) when (command.PreviewOnly)
        {
            return new(correctionId, null, EmptyPreview(ex.Refusal), false);
        }
    }

    private static void ValidateShape(ReassignAllocation command)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.ReassignAllocation, command.Reason);
        if (!AllocationReassignmentMode.All.Contains(command.Mode))
            throw new CommandRefusedException("reassignment_mode_unknown", $"'{command.Mode}' is not an allocation reassignment mode.");
        if (!IsProductionOrder(command.FromProductionOrder) || !IsProductionOrder(command.ToProductionOrder))
            throw new CommandRefusedException("production_order_required", "Both sides of an allocation reassignment must be production_order documents.", "choose two production orders");
        if (ReassignAllocationRules.SameDocument(command.FromProductionOrder, command.ToProductionOrder))
            throw new CommandRefusedException("orders_must_differ", "The from and to production orders must differ.");
        if (command.Mode == AllocationReassignmentMode.PairwiseExchange && command.ToSerials is null)
            throw new CommandRefusedException("exchange_serials_required", "A pairwise exchange needs serials leaving both production orders.");
        if (command.Mode == AllocationReassignmentMode.Move && command.ToSerials is not null)
            throw new CommandRefusedException("exchange_serials_not_allowed", "A move accepts serials only from the from-order.");
    }

    private static bool IsProductionOrder(DocumentRef document) =>
        document.Id != Guid.Empty && document.Type == ReassignAllocationRules.ProductionOrderDocumentType;

    private static async Task<ProductionOrderAllocation> LoadOrderAsync(DocumentRef order, IProductionOrderAllocationPort port, CancellationToken cancellationToken)
    {
        var facts = await port.GetAsync(order, cancellationToken)
            ?? throw new CommandRefusedException("production_order_unknown", $"Production order {order.Id} is not available.", "select an existing production order");
        if (!ReassignAllocationRules.SameDocument(facts.ProductionOrder, order))
            throw new CommandRefusedException("production_order_mismatch", "Manufacturing returned allocation facts for another production order.");
        if (facts.Quantity < 0) throw new CommandRefusedException("production_order_quantity_invalid", "Manufacturing returned a negative production-order quantity.");
        return facts;
    }

    private static List<SelectedSerial> Expand(SelectionSource selection, SerialSchemeDefinition scheme)
    {
        if (selection.Kind == SelectionSource.Kinds.PastedList)
        {
            if (selection.Serials is not { Count: > 0 }) throw new CommandRefusedException("serial_selection_empty", "A pasted serial selection must contain at least one serial.");
            if (selection.Serials.Count > SelectionSetResolverMax) throw new CommandRefusedException("serial_selection_too_large", $"A selection cannot exceed {SelectionSetResolverMax:N0} serials.");
            var result = new List<SelectedSerial>(selection.Serials.Count);
            foreach (var input in selection.Serials)
            {
                var parsed = scheme.TryParse(input) ?? throw new CommandRefusedException("serial_not_in_scheme", $"'{input}' is not a serial of scheme {scheme.Code}.", "scan again or choose the correct production order");
                result.Add(new(parsed.Ordinal, scheme.FormatOrdinal(parsed.Ordinal), Normalised(scheme, parsed.Ordinal)));
            }
            return result;
        }

        if (selection.Kind == SelectionSource.Kinds.SerialRange)
        {
            if (!string.Equals(selection.Scheme, scheme.Code, StringComparison.OrdinalIgnoreCase))
                throw new CommandRefusedException("selection_scheme_differs", $"The selection uses scheme '{selection.Scheme}', but the orders use '{scheme.Code}'.");
            var start = scheme.TryParse(selection.RangeStart ?? "") ?? throw new CommandRefusedException("range_invalid", $"'{selection.RangeStart}' is not a serial of scheme {scheme.Code}.");
            var end = scheme.TryParse(selection.RangeEnd ?? "") ?? throw new CommandRefusedException("range_invalid", $"'{selection.RangeEnd}' is not a serial of scheme {scheme.Code}.");
            if (start.Block != end.Block)
                throw new CommandRefusedException("range_crosses_block", $"{selection.RangeStart} → {selection.RangeEnd} crosses the {scheme.BlockFormat} boundary of scheme {scheme.Code}; a range expands within one block only.", "enter two ranges, or paste a list");
            if (end.Counter < start.Counter) throw new CommandRefusedException("range_invalid", "The range end is before its start.");
            var count = end.Counter - start.Counter + 1;
            if (count > SelectionSetResolverMax) throw new CommandRefusedException("serial_selection_too_large", $"A selection cannot exceed {SelectionSetResolverMax:N0} serials.");
            return Enumerable.Range(0, checked((int)count)).Select(offset =>
            {
                var ordinal = start.Ordinal + offset;
                return new SelectedSerial(ordinal, scheme.FormatOrdinal(ordinal), Normalised(scheme, ordinal));
            }).ToList();
        }

        throw new CommandRefusedException("serial_selection_kind_invalid", "Allocation reassignment accepts a pasted serial list or a serial range.", "scan, paste a list, or enter a range expression");
    }

    private static void ValidateSelections(ReassignAllocation command, IReadOnlyList<SelectedSerial> from, IReadOnlyList<SelectedSerial> exchange)
    {
        if (from.GroupBy(s => s.Ordinal).Any(g => g.Count() > 1) || exchange.GroupBy(s => s.Ordinal).Any(g => g.Count() > 1))
            throw new CommandRefusedException("serial_repeated", "A serial may appear only once on each side of a reassignment.", "remove the duplicate serial");
        if (from.Select(s => s.Ordinal).Intersect(exchange.Select(s => s.Ordinal)).Any())
            throw new CommandRefusedException("serial_on_both_sides", "The same serial cannot leave both production orders.", "remove it from one side");
        if (command.Mode == AllocationReassignmentMode.PairwiseExchange && from.Count != exchange.Count)
            throw new CommandRefusedException("pair_count_differs", $"A pairwise exchange needs equal counts; {from.Count} serials leave the from-order and {exchange.Count} leave the to-order.", "remove unmatched serials or add their partners");
    }

    private static async Task<IReadOnlyList<PlannedPair>> BuildPlanAsync(
        ReassignAllocation command,
        ProductionOrderAllocation sourceOrder,
        ProductionOrderAllocation targetOrder,
        SerialSchemeDefinition scheme,
        IReadOnlyList<SelectedSerial> from,
        IReadOnlyList<SelectedSerial> exchange,
        SerialAllocationReservationStore store,
        SerialisationDbContext db,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var all = from.Concat(exchange).ToList();
        var resolved = await store.ResolveAsync(scheme.Code, sourceOrder.ItemId, all.Select(s => s.Ordinal).ToList(), !command.PreviewOnly, cancellationToken);
        var missing = all.FirstOrDefault(s => !resolved.ContainsKey(s.Ordinal));
        if (missing is not null)
            throw new CommandRefusedException("serial_not_reserved", $"Serial {missing.Value} is not reserved by a range for this item and scheme.", "remove it or correct the range allocation first");

        var commissioned = resolved.Where(r => r.Value.Commissioned).Select(r => r.Key).ToHashSet();
        foreach (var chunk in all.Select(s => s.Normalised).Distinct(StringComparer.Ordinal).Chunk(2000))
        {
            var rows = await (from identifier in db.Identifiers.AsNoTracking()
                              join assignment in db.ItemAssignments.AsNoTracking() on new { identifier.TenantId, identifier.UnitId } equals new { assignment.TenantId, assignment.UnitId }
                              where identifier.TenantId == tenantId && identifier.Type == scheme.IdentifierType
                                    && assignment.ItemId == sourceOrder.ItemId && chunk.Contains(identifier.NormalisedValue)
                              select identifier.NormalisedValue).Distinct().ToListAsync(cancellationToken);
            foreach (var value in rows)
                foreach (var serial in all.Where(s => s.Normalised == value)) commissioned.Add(serial.Ordinal);
        }

        var pairs = new List<PlannedPair>(from.Count);
        for (var index = 0; index < from.Count; index++)
        {
            var fromMove = Move(from[index], resolved[from[index].Ordinal], command.FromProductionOrder, command.ToProductionOrder, commissioned);
            PlannedMove? exchangeMove = null;
            if (command.Mode == AllocationReassignmentMode.PairwiseExchange)
                exchangeMove = Move(exchange[index], resolved[exchange[index].Ordinal], command.ToProductionOrder, command.FromProductionOrder, commissioned);
            pairs.Add(new(index + 1, fromMove, exchangeMove));
        }

        if (command.Mode == AllocationReassignmentMode.Move)
        {
            var targetCount = await store.EffectiveCountAsync(command.ToProductionOrder, cancellationToken);
            if (targetCount + pairs.Count > targetOrder.Quantity)
                throw new CommandRefusedException("target_order_quantity_exceeded",
                    $"Moving {pairs.Count} serials would reserve {targetCount + pairs.Count} against production-order quantity {targetOrder.Quantity}.",
                    ReassignAllocationRules.QuantityUnblockingStep);
        }

        return pairs;
    }

    private static PlannedMove Move(SelectedSerial serial, SerialAllocationReservationStore.Reservation reservation,
        DocumentRef expected, DocumentRef target, HashSet<long> commissioned)
    {
        var current = reservation.EffectiveOrder
            ?? throw new CommandRefusedException("serial_not_allocated", $"Serial {serial.Value} has no production-order reservation.", "allocate it before reassigning it");
        if (!ReassignAllocationRules.SameDocument(current, expected))
            throw new CommandRefusedException("serial_not_allocated_to_order", $"Serial {serial.Value} is reserved for production order {current.Id}, not {expected.Id}.", "remove it or select its current production order");
        return new(serial, reservation, current, target, commissioned.Contains(serial.Ordinal));
    }

    private static async Task ValidateReversalAsync(CorrectionMetadata metadata, IReadOnlyList<PlannedPair> plan,
        SerialisationDbContext db, TenantId tenantId, CancellationToken cancellationToken)
    {
        if (metadata.CorrectsCorrectionId is not { } correctedId) return;
        var prior = await db.AllocationReassignments.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CorrectionId == correctedId).ToListAsync(cancellationToken);
        if (prior.Count == 0)
            throw new CommandRefusedException("corrected_correction_not_allocation_reassignment", $"Correction {correctedId} did not reassign an allocation.", "select an earlier ReassignAllocation correction");
        var moves = plan.SelectMany(p => p.Moves).ToList();
        if (prior.Count != moves.Count || !prior.Select(r => (r.Scheme, r.SerialOrdinal)).Order().SequenceEqual(moves.Select(m => (m.Reservation.Range.Scheme, m.Serial.Ordinal)).Order()))
            throw new CommandRefusedException("reversal_serial_set_differs", "A reversal must contain exactly every serial moved by the correction it references.", "select every serial from the earlier reassignment");
        foreach (var move in moves)
        {
            var before = prior.Single(r => r.Scheme == move.Reservation.Range.Scheme && r.SerialOrdinal == move.Serial.Ordinal);
            if (!ReassignAllocationRules.SameDocument(move.Previous, New(before)) || !ReassignAllocationRules.SameDocument(move.Target, Previous(before)))
                throw new CommandRefusedException("reversal_does_not_restore", $"Serial {move.Serial.Value} would not return to its order before correction {correctedId}.", "swap the from and to orders and select the complete earlier set");
        }
    }

    private static CorrectionPreview Preview(IReadOnlyList<PlannedPair> plan)
    {
        var rows = plan.Select(pair => new CorrectionAllocationPreview(
            pair.Pair,
            pair.From.Serial.Value,
            pair.Exchange?.Serial.Value,
            pair.From.Reservation.Range.RangeId,
            pair.Exchange?.Reservation.Range.RangeId,
            pair.From.Previous,
            pair.From.Target,
            pair.Blocked ? AllocationReassignmentOutcome.Blocked : AllocationReassignmentOutcome.WillMove,
            pair.Blocked ? ReassignAllocationRules.AlreadyCommissioned : null,
            pair.Blocked ? "One or both serials in this pair have already been commissioned." : null,
            pair.Blocked ? ReassignAllocationRules.CommissionedUnblockingStep : null))
            .OrderBy(row => row.Outcome == AllocationReassignmentOutcome.Blocked ? 0 : 1)
            .ThenBy(row => row.Pair).ToList();
        return new(null, CorrectionCommand.ReassignAllocation, 0,
            rows.Count(r => r.Outcome == AllocationReassignmentOutcome.WillMove), 0,
            rows.Count(r => r.Outcome == AllocationReassignmentOutcome.Blocked), [], [], [], [], [], null, rows);
    }

    private static CorrectionPreview EmptyPreview(CommandRefusal refusal) =>
        new(null, CorrectionCommand.ReassignAllocation, 0, 0, 0, 1, [], [], [], [], [], refusal, []);

    private static async Task<ReassignAllocationResult?> ReplayAsync(ReassignAllocation command, Guid correctionId,
        IReadOnlyList<SelectedSerial> from, IReadOnlyList<SelectedSerial> exchange, SerialisationDbContext db,
        TenantId tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.AllocationReassignments.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CommandId == command.CommandId)
            .OrderBy(r => r.Pair).ThenBy(r => r.SerialOrdinal).ToListAsync(cancellationToken);
        if (rows.Count == 0) return null;
        var requested = from.Select(s => new RequestedMove(s, command.FromProductionOrder, command.ToProductionOrder))
            .Concat(exchange.Select(s => new RequestedMove(s, command.ToProductionOrder, command.FromProductionOrder)))
            .OrderBy(m => m.Serial.Ordinal).ToList();
        var recorded = rows.OrderBy(r => r.SerialOrdinal).ToList();
        var differs = recorded.Count != requested.Count;
        for (var index = 0; !differs && index < recorded.Count; index++)
        {
            differs = recorded[index].CorrectionId != correctionId || recorded[index].Mode != command.Mode
                || recorded[index].SerialOrdinal != requested[index].Serial.Ordinal
                || !ReassignAllocationRules.SameDocument(Previous(recorded[index]), requested[index].Previous)
                || !ReassignAllocationRules.SameDocument(New(recorded[index]), requested[index].Target);
        }
        if (differs)
            throw new CommandRefusedException("command_id_reused", $"Command {command.CommandId} already recorded a different allocation reassignment.", "send a new command id");

        var previews = rows.GroupBy(r => r.Pair).OrderBy(g => g.Key).Select(group =>
        {
            var first = group.Single(r => ReassignAllocationRules.SameDocument(Previous(r), command.FromProductionOrder));
            var other = group.SingleOrDefault(r => r.ReassignmentId != first.ReassignmentId);
            return new CorrectionAllocationPreview(group.Key, first.SerialValue, other?.SerialValue, first.SerialRangeId,
                other?.SerialRangeId, command.FromProductionOrder, command.ToProductionOrder, AllocationReassignmentOutcome.WillMove);
        }).ToList();
        var preview = new CorrectionPreview(null, CorrectionCommand.ReassignAllocation, 0, previews.Count, 0, 0,
            [], [], [], [], [], null, previews);
        return new(correctionId, rows[0].EventId, preview, true);
    }

    private static string Normalised(SerialSchemeDefinition scheme, long ordinal) =>
        scheme.FormatOrdinal(ordinal).Replace(scheme.Separator, "", StringComparison.Ordinal).ToUpperInvariant();

    private static CommandRefusedException Refused(CommandRefusal refusal) =>
        new(refusal.Rule, refusal.Message, refusal.UnblockingStep);

    private static DocumentRef Previous(SerialAllocationReassignment row) =>
        new(row.PreviousProductionOrderType, row.PreviousProductionOrderId, row.PreviousProductionOrderLine);

    private static DocumentRef New(SerialAllocationReassignment row) =>
        new(row.NewProductionOrderType, row.NewProductionOrderId, row.NewProductionOrderLine);

    private const int SelectionSetResolverMax = 100_000;
    private sealed record SelectedSerial(long Ordinal, string Value, string Normalised);
    private sealed record PlannedMove(SelectedSerial Serial, SerialAllocationReservationStore.Reservation Reservation,
        DocumentRef Previous, DocumentRef Target, bool Commissioned);
    private sealed record PlannedPair(int Pair, PlannedMove From, PlannedMove? Exchange)
    {
        public bool Blocked => From.Commissioned || Exchange?.Commissioned == true;
        public IEnumerable<PlannedMove> Moves
        {
            get
            {
                yield return From;
                if (Exchange is not null) yield return Exchange;
            }
        }
    }
    private sealed record RequestedMove(SelectedSerial Serial, DocumentRef Previous, DocumentRef Target);
}
