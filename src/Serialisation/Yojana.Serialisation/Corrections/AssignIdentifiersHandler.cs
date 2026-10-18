using System.Text.Json;
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
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Corrections;

/// <summary>Pure pairing rules; validation of the paired value stays behind the item and identifier ports.</summary>
public static class AssignIdentifiersRules
{
    public sealed record Pair(int Index, Guid? RequestedUnitId, Guid? UnitId, string Value, string? Problem = null);

    public static IReadOnlyList<Pair> PairLines(string mode, IReadOnlyList<Guid> allocationOrder, IReadOnlySet<Guid> orderUnits, IReadOnlyList<IdentifierAssignmentInput> lines)
    {
        if (!IdentifierAssignmentMode.All.Contains(mode)) throw new CommandRefusedException("assignment_mode_unknown", $"'{mode}' is not one of {string.Join(", ", IdentifierAssignmentMode.All)}.");
        var result = new List<Pair>(lines.Count);
        var used = new HashSet<Guid>();
        var next = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (mode == IdentifierAssignmentMode.AllocationOrder)
            {
                if (line.UnitId is not null)
                {
                    result.Add(new(index, line.UnitId, null, line.Value, "unit_not_allowed_in_value_list"));
                    continue;
                }

                result.Add(next < allocationOrder.Count
                    ? new Pair(index, null, allocationOrder[next++], line.Value)
                    : new Pair(index, null, null, line.Value, "no_provisional_unit_available"));
                continue;
            }

            if (line.UnitId is not { } unitId)
            {
                result.Add(new(index, null, null, line.Value, "unit_required_for_explicit_pair"));
            }
            else if (!orderUnits.Contains(unitId))
            {
                result.Add(new(index, unitId, null, line.Value, "unit_not_on_production_order"));
            }
            else if (!used.Add(unitId))
            {
                result.Add(new(index, unitId, unitId, line.Value, "unit_repeated_in_list"));
            }
            else
            {
                result.Add(new(index, unitId, unitId, line.Value));
            }
        }

        return result;
    }
}

/// <summary>
/// P3-T10 / case 135. Production-order membership is read from the unit's creating ledger fact; allocation order is the
/// creating command's position then its commissioning line. The service changes identifiers only through one event per assigned unit, while flag-only remainder
/// changes use the existing overlay-flag event contract. Correction metadata is present on every fact and recorded once.
/// </summary>
public sealed class AssignIdentifiersService(
    SerialisationDbContext db,
    IUnitEventWriter writer,
    IdentifierService identifiers,
    IItemQueryPort items,
    ISerialSchemeSource schemes,
    CorrectionLedger corrections,
    CorrectionApprovalSettings approvalSettings,
    AwaitingVendorSerialQueries awaitingQueries,
    IMessageBus bus,
    ITenantContext tenant,
    IClock clock)
{
    private const string ProductionOrderType = "production_order";

    public async Task<AssignIdentifiersPreview> PreviewAsync(AssignIdentifiers command, CancellationToken cancellationToken)
    {
        var evaluation = await EvaluateAsync(command, cancellationToken);
        return Preview(evaluation, command);
    }

    public async Task<AssignIdentifiersResult> ExecuteAsync(AssignIdentifiers command, CancellationToken cancellationToken)
    {
        var correctionId = command.Correction?.CorrectionId ?? command.CommandId;
        if (await db.Corrections.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == correctionId, cancellationToken) is { } existing)
        {
            return await ReplayAsync(command, existing, cancellationToken);
        }

        var evaluation = await EvaluateAsync(command, cancellationToken);
        if (evaluation.Work.Count == 0)
        {
            throw new CommandRefusedException("no_assignments", "No identifier line can be assigned; nothing was recorded.", "correct the rejected lines and preview again");
        }

        if (evaluation.ApprovalRefusal is { } refusal) throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);

        var summaryEventId = TimeOrderedId.New(command.EventTime);
        var now = clock.UtcNow;
        var drafts = new List<UnitEventDraft>();
        var published = new List<PublishedIdentifierAssignment>();
        var resultLines = evaluation.Lines.ToArray();
        foreach (var work in evaluation.Work)
        {
            var unit = await db.Units.FirstAsync(u => u.TenantId == tenant.TenantId && u.UnitId == work.UnitId, cancellationToken);
            var open = await db.Identifiers.FirstAsync(i => i.TenantId == tenant.TenantId && i.IdentifierId == work.OpenIdentifierId && i.ValidTo == null, cancellationToken);
            var eventId = TimeOrderedId.New(command.EventTime);
            UnitIdentifier replacement;
            if (evaluation.Reversal)
            {
                var priorProvisional = await db.Identifiers.AsNoTracking().FirstAsync(i => i.TenantId == tenant.TenantId && i.IdentifierId == work.RestoreIdentifierId, cancellationToken);
                replacement = CloneProvisional(priorProvisional, unit.UnitId, command, eventId, now);
            }
            else
            {
                replacement = work.Prepared!.ToRow(tenant.TenantId, unit.UnitId, command.EventTime, command.ActorRef, now, eventId);
            }

            replacement.SupersedesIdentifierId = open.IdentifierId;
            open.ValidTo = command.EventTime;
            open.SupersededByIdentifierId = replacement.IdentifierId;
            open.CorrectionReason = command.Reason;
            db.Identifiers.Add(replacement);

            var flags = Flags(unit);
            if (evaluation.Reversal)
            {
                if (!flags.Contains(OverlayFlag.AwaitingVendorSerial)) flags.Add(OverlayFlag.AwaitingVendorSerial);
            }
            else
            {
                flags.Remove(OverlayFlag.AwaitingVendorSerial);
            }

            unit.OverlayFlags = JsonSerializer.Serialize(flags, LedgerJson.Options);
            drafts.Add(new UnitEventDraft
            {
                UnitId = unit.UnitId,
                EventTime = command.EventTime,
                EventType = UnitEventType.IdentifierAssigned,
                PresetEventId = eventId,
                BizStep = "commissioning",
                LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef,
                DocumentType = command.ProductionOrder.Type,
                DocumentId = command.ProductionOrder.Id,
                DocumentLine = command.ProductionOrder.Line,
                IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unit.UnitId, UnitEventType.IdentifierAssigned, command.EventTime),
                Attributes = CorrectionLedger.Attributes(evaluation.Metadata, new
                {
                    command_id = command.CommandId,
                    summary_event_id = summaryEventId,
                    assignment_index = work.LineIndex,
                    production_order = command.ProductionOrder,
                    reversal = evaluation.Reversal,
                    before = IdentifierFact(open),
                    after = IdentifierFact(replacement),
                    warnings = work.Warnings,
                }),
                Delta = new CurrentStateDelta { OverlayFlags = flags },
            });
            published.Add(new(unit.UnitId, replacement.IdentifierId, new(replacement.Type, replacement.Scheme, replacement.NormalisedValue, replacement.IssuerPartyRef)));
            resultLines[work.LineIndex] = resultLines[work.LineIndex] with
            {
                Outcome = evaluation.Reversal ? IdentifierAssignmentOutcome.Reversed : IdentifierAssignmentOutcome.Assigned,
                IdentifierId = replacement.IdentifierId,
            };
        }

        foreach (var unitId in evaluation.RemainingUnitIds)
        {
            var unit = await db.Units.FirstAsync(u => u.TenantId == tenant.TenantId && u.UnitId == unitId, cancellationToken);
            var flags = Flags(unit);
            if (flags.Contains(OverlayFlag.AwaitingVendorSerial)) continue;
            flags.Add(OverlayFlag.AwaitingVendorSerial);
            unit.OverlayFlags = JsonSerializer.Serialize(flags, LedgerJson.Options);
            drafts.Add(new UnitEventDraft
            {
                UnitId = unitId,
                EventTime = command.EventTime,
                EventType = UnitEventType.FlagSet,
                LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef,
                DocumentType = command.ProductionOrder.Type,
                DocumentId = command.ProductionOrder.Id,
                DocumentLine = command.ProductionOrder.Line,
                IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unitId, UnitEventType.FlagSet, command.EventTime),
                Attributes = CorrectionLedger.Attributes(evaluation.Metadata, new
                {
                    command_id = command.CommandId,
                    summary_event_id = summaryEventId,
                    flag = OverlayFlag.AwaitingVendorSerial,
                    reason = "vendor_serial_missing",
                    flags_after = flags,
                }),
                Delta = new CurrentStateDelta { OverlayFlags = flags },
            });
        }

        await writer.AppendAsync(drafts, cancellationToken);
        await corrections.RecordAsync(CorrectionCommand.AssignIdentifiers, evaluation.Metadata, command.ActorRef, command.EventTime, drafts.Select(d => d.UnitId).Distinct().ToList(), cancellationToken);
        var summary = new UnitIdentifiersAssigned(
            new(UnitIdentifiersAssigned.Schema, UnitIdentifiersAssigned.Version, summaryEventId, command.EventTime, now, command.LegalEntityId, command.ActorRef, command.ProductionOrder),
            PublishedCorrection(evaluation.Metadata), command.ProductionOrder, published, evaluation.RemainingUnitIds.Count, evaluation.RemainingUnitIds);
        await bus.PublishAsync(summary, new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return new(evaluation.Metadata.CorrectionId, published.Count, resultLines, evaluation.RemainingUnitIds.Count, evaluation.RemainingUnitIds, Reversal: evaluation.Reversal);
    }

    private async Task<Evaluation> EvaluateAsync(AssignIdentifiers command, CancellationToken cancellationToken)
    {
        ValidateEnvelope(command);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.AssignIdentifiers, command.Correction, command.CommandId, command.Reason, command.Note,
            null, command.EventTime, CorrectionInitiatingRole.ProductionSupervisor, cancellationToken);
        if (metadata.CorrectsCorrectionId is { } priorId)
        {
            var prior = await db.Corrections.AsNoTracking().FirstAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == priorId, cancellationToken);
            if (prior.Command == CorrectionCommand.AssignIdentifiers)
            {
                if (command.Lines.Count != 0) throw new CommandRefusedException("reversal_lines_not_empty", "An AssignIdentifiers reversal restores the earlier provisional identifiers and takes no new values.");
                return await EvaluateReversalAsync(command, metadata, priorId, cancellationToken);
            }
        }

        if (command.Lines.Count == 0) throw new CommandRefusedException("identifier_list_empty", "Give at least one identifier value.");
        var orderUnits = await LoadOrderUnitsAsync(command.ProductionOrder, cancellationToken);
        var eligible = orderUnits.Where(Eligible).OrderBy(u => u.AllocationSequence).Select(u => u.UnitId).ToList();
        var pairs = AssignIdentifiersRules.PairLines(command.Mode, eligible, orderUnits.Select(u => u.UnitId).ToHashSet(), command.Lines);
        var byId = orderUnits.ToDictionary(u => u.UnitId);
        var profiles = new Dictionary<Guid, ItemSerialisationProfile?>();
        var planned = new List<PlannedLine>(pairs.Count);
        foreach (var pair in pairs)
        {
            if (pair.Problem is not null || pair.UnitId is not { } unitId)
            {
                planned.Add(Rejected(pair, pair.Problem!));
                continue;
            }

            var orderUnit = byId[unitId];
            if (LifecycleProblem(orderUnit.LifecycleStatus) is { } lifecycle)
            {
                planned.Add(Rejected(pair, lifecycle));
                continue;
            }

            if (!Eligible(orderUnit))
            {
                planned.Add(Rejected(pair, "unit_not_provisional_only"));
                continue;
            }

            if (!profiles.TryGetValue(orderUnit.ItemId, out var profile))
            {
                profile = await items.GetAsync(orderUnit.ItemId, cancellationToken);
                profiles[orderUnit.ItemId] = profile;
            }

            if (profile is null)
            {
                planned.Add(Rejected(pair, "item_unknown"));
                continue;
            }

            var scheme = schemes.Find(profile.SerialScheme);
            if (scheme is null)
            {
                planned.Add(Rejected(pair, "serial_scheme_unknown"));
                continue;
            }

            if (scheme.IdentifierType == IdentifierType.Provisional)
            {
                planned.Add(Rejected(pair, "real_identifier_scheme_required"));
                continue;
            }

            var prepared = identifiers.Prepare(new(scheme.IdentifierType, pair.Value, command.IssuerPartyRef, command.Qualifier), orderUnit.ItemId, null);
            if (scheme.TryParse(pair.Value) is null && scheme.TryParse(prepared.Normalised) is null)
            {
                planned.Add(Rejected(pair, "value_not_in_item_serial_scheme", prepared.Normalised));
                continue;
            }

            if (prepared.Blocks)
            {
                planned.Add(Rejected(pair, "identifier_invalid", prepared.Normalised, warnings: prepared.Validation.Reasons));
                continue;
            }

            planned.Add(new(pair, orderUnit, prepared, null, prepared.Validation.IsValid ? [] : prepared.Validation.Reasons));
        }

        var seen = new HashSet<(string Scheme, string Scope, string Value)>();
        foreach (var line in planned.Where(p => p.Prepared is not null).OrderBy(p => p.Pair.Index))
        {
            var key = (line.Prepared!.Policy.Scheme, line.Prepared.ScopeKey, line.Prepared.Normalised);
            if (!seen.Add(key)) line.Problem = "duplicate_value_in_list";
        }

        var values = planned.Where(p => p.Prepared is not null && p.Problem is null).Select(p => p.Prepared!.Normalised).ToList();
        var holders = await identifiers.OpenHoldersOfValuesAsync(values, cancellationToken);
        foreach (var line in planned.Where(p => p.Prepared is not null && p.Problem is null))
        {
            var hit = holders.FirstOrDefault(h => h.Scheme == line.Prepared!.Policy.Scheme && h.ScopeKey == line.Prepared.ScopeKey && h.NormalisedValue == line.Prepared.Normalised);
            if (hit.Holder is null) continue;
            line.Problem = hit.Holder.UnitId == line.Unit!.UnitId ? "value_matches_provisional" : "identifier_in_use";
            line.ConflictingUnitId = hit.Holder.UnitId;
        }

        var work = planned.Where(p => p.Prepared is not null && p.Problem is null).Select(p => new Work(
            p.Pair.Index, p.Unit!.UnitId, p.Unit.OpenIdentifiers[0].IdentifierId, null, p.Prepared, p.Warnings)).ToList();
        var successful = work.Select(w => w.UnitId).ToHashSet();
        var remaining = orderUnits.Where(Eligible).Select(u => u.UnitId).Where(id => !successful.Contains(id)).ToList();
        var results = planned.OrderBy(p => p.Pair.Index).Select(p => new IdentifierAssignmentLineResult(p.Pair.Index, p.Pair.RequestedUnitId, p.Pair.UnitId,
            p.Pair.Value, p.Prepared?.Normalised ?? p.Normalised, p.Problem is null ? IdentifierAssignmentOutcome.Assign : IdentifierAssignmentOutcome.Rejected,
            p.Problem, p.ConflictingUnitId, Warnings: p.Warnings)).ToList();
        var refusal = work.Count == 0
            ? new CommandRefusal("no_assignments", "No identifier line can be assigned; nothing would be recorded.", "correct the rejected lines and preview again")
            : await ApprovalRefusalAsync(command, metadata, work.Select(w => w.UnitId).ToList(), cancellationToken);
        return new(metadata, results, work, remaining, false, refusal, orderUnits);
    }

    private async Task<Evaluation> EvaluateReversalAsync(AssignIdentifiers command, CorrectionMetadata metadata, Guid priorCorrectionId, CancellationToken cancellationToken)
    {
        var facts = await CorrectionFactsAsync(priorCorrectionId, cancellationToken);
        if (facts.Count == 0) throw new CommandRefusedException("reversal_facts_missing", $"Correction {priorCorrectionId} has no live identifier-assignment facts.");
        if (facts.Any(f => f.ProductionOrderId != command.ProductionOrder.Id || f.ProductionOrderType != command.ProductionOrder.Type))
            throw new CommandRefusedException("production_order_mismatch", "The reversal must name the production order of the correction it reverses.");

        var orderUnits = await LoadOrderUnitsAsync(command.ProductionOrder, cancellationToken);
        var byId = orderUnits.ToDictionary(u => u.UnitId);
        var work = new List<Work>();
        var lines = new List<IdentifierAssignmentLineResult>();
        foreach (var fact in facts.OrderBy(f => f.AssignmentIndex))
        {
            if (!byId.TryGetValue(fact.UnitId, out var unit)) throw new CommandRefusedException("reversal_unit_not_on_order", $"Unit {fact.UnitId} is no longer resolved by the production order.");
            var current = unit.OpenIdentifiers.SingleOrDefault(i => i.IdentifierId == fact.AfterIdentifierId)
                ?? throw new CommandRefusedException("reversal_state_changed", $"Unit {fact.UnitId} no longer carries identifier {fact.AfterIdentifierId}; reverse its later correction first.");
            if (LifecycleProblem(unit.LifecycleStatus) is { } lifecycle) throw new CommandRefusedException(lifecycle, $"Unit {unit.UnitId} is {unit.LifecycleStatus} and cannot be changed by AssignIdentifiers.");
            var reversalIndex = lines.Count;
            work.Add(new(reversalIndex, fact.UnitId, current.IdentifierId, fact.BeforeIdentifierId, null, []));
            lines.Add(new(reversalIndex, fact.UnitId, fact.UnitId, fact.BeforeRawValue, fact.BeforeNormalisedValue, IdentifierAssignmentOutcome.Assign));
        }

        var restored = work.Select(w => w.UnitId).ToHashSet();
        var remaining = orderUnits.Where(u => Eligible(u) || restored.Contains(u.UnitId)).Select(u => u.UnitId).ToList();
        var refusal = await ApprovalRefusalAsync(command, metadata, work.Select(w => w.UnitId).ToList(), cancellationToken);
        return new(metadata, lines.OrderBy(l => l.Index).ToList(), work, remaining, true, refusal, orderUnits);
    }

    private async Task<CommandRefusal?> ApprovalRefusalAsync(AssignIdentifiers command, CorrectionMetadata metadata, List<Guid> targetUnits, CancellationToken cancellationToken)
    {
        if (command.CommittedBy is not { } committer) return new("committer_required", "The correction names who commits it and the roles they hold.", "sign in and preview again");
        var shipped = targetUnits.Count > 0 &&
            (await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenant.TenantId && targetUnits.Contains(e.UnitId) && e.EventType == UnitEventType.Shipped, cancellationToken)
             || await db.ArchivedEvents.AsNoTracking().AnyAsync(e => e.TenantId == tenant.TenantId && targetUnits.Contains(e.UnitId) && e.EventType == UnitEventType.Shipped, cancellationToken));
        var decision = CorrectionApprovalRules.Evaluate(await approvalSettings.GetAsync(cancellationToken), CorrectionCommand.AssignIdentifiers, new(UnitHasShipped: shipped), committer.Roles);
        if (decision.Status == CorrectionApprovalStatus.Refused) return decision.Refusal;
        if (decision.Status != CorrectionApprovalStatus.NeedsApproval) return null;
        var approvers = metadata.Approvers!.Where(a => !string.Equals(a.UserRef, committer.UserRef, StringComparison.Ordinal)).ToList();
        return decision.ApproverRoles.All(role => approvers.Any(a => a.Roles.Contains(role, StringComparer.Ordinal))) ? null : decision.Refusal;
    }

    private AssignIdentifiersPreview Preview(Evaluation evaluation, AssignIdentifiers command)
    {
        var events = evaluation.Work.Select(w => new CorrectionEventPreview(w.UnitId, UnitEventType.IdentifierAssigned, command.EventTime,
            evaluation.Reversal ? "restore provisional identifier" : $"assign line {w.LineIndex + 1}")).ToList();
        var alreadyFlagged = evaluation.OrderUnits.Where(u => u.Flags.Contains(OverlayFlag.AwaitingVendorSerial)).Select(u => u.UnitId).ToHashSet();
        events.AddRange(evaluation.RemainingUnitIds.Where(id => !alreadyFlagged.Contains(id)).Select(id => new CorrectionEventPreview(id, UnitEventType.FlagSet, command.EventTime, OverlayFlag.AwaitingVendorSerial)));
        var outcomes = evaluation.Lines.Select(line => new UnitOutcome(line.UnitId ?? Guid.Empty, line.NormalisedValue ?? line.RawValue,
            line.Outcome == IdentifierAssignmentOutcome.Assign ? UnitOutcome.Outcomes.Change : UnitOutcome.Outcomes.Block, line.Reason,
            line.UnitId is null ? null : evaluation.Reversal ? "restore provisional identifier" : "close provisional identifier and open the item-scheme identifier")).ToList();
        foreach (var id in evaluation.RemainingUnitIds.Where(id => evaluation.Lines.All(l => l.UnitId != id) && !alreadyFlagged.Contains(id)))
            outcomes.Add(new(id, null, UnitOutcome.Outcomes.Change, null, $"set {OverlayFlag.AwaitingVendorSerial}"));
        var preview = new CorrectionPreview(null, CorrectionCommand.AssignIdentifiers, outcomes.Select(o => o.UnitId).Where(id => id != Guid.Empty).Distinct().Count(),
            outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Change), 0, outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Block), outcomes, events, [], [], [], evaluation.ApprovalRefusal);
        return new(preview, evaluation.Lines, evaluation.RemainingUnitIds.Count, evaluation.RemainingUnitIds, evaluation.Reversal);
    }

    private async Task<AssignIdentifiersResult> ReplayAsync(AssignIdentifiers command, CorrectionRecord existing, CancellationToken cancellationToken)
    {
        if (existing.Command != CorrectionCommand.AssignIdentifiers) throw new CommandRefusedException("correction_id_in_use", $"Correction id {existing.CorrectionId} has already been used by {existing.Command}.");
        var facts = (await CorrectionFactsAsync(existing.CorrectionId, cancellationToken)).Where(f => f.CommandId == command.CommandId).OrderBy(f => f.AssignmentIndex).ToList();
        if (facts.Count == 0) throw new CommandRefusedException("correction_id_in_use", $"Correction id {existing.CorrectionId} belongs to a different command.");
        var report = await awaitingQueries.ByProductionOrderAsync(command.ProductionOrder, cancellationToken);
        var lines = facts.Select(f => new IdentifierAssignmentLineResult(f.AssignmentIndex, f.UnitId, f.UnitId, f.AfterRawValue, f.AfterNormalisedValue,
            f.Reversal ? IdentifierAssignmentOutcome.Reversed : IdentifierAssignmentOutcome.Assigned, IdentifierId: f.AfterIdentifierId)).ToList();
        return new(existing.CorrectionId, facts.Count, lines, report.Count, report.Units.Select(u => u.UnitId).ToList(), true, facts.All(f => f.Reversal));
    }

    private async Task<List<OrderUnit>> LoadOrderUnitsAsync(DocumentRef productionOrder, CancellationToken cancellationToken)
    {
        var allocations = await CreationFactsAsync(productionOrder, cancellationToken);
        var unitIds = allocations.Select(a => a.UnitId).Distinct().ToList();
        if (unitIds.Count == 0) return [];
        var units = await db.Units.AsNoTracking().Where(u => u.TenantId == tenant.TenantId && unitIds.Contains(u.UnitId)).ToListAsync(cancellationToken);
        var itemRows = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenant.TenantId && unitIds.Contains(a.UnitId) && a.ValidTo == null).ToListAsync(cancellationToken);
        var open = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && unitIds.Contains(i.UnitId) && i.ValidTo == null).ToListAsync(cancellationToken);
        var sequence = allocations.GroupBy(a => a.UnitId).ToDictionary(g => g.Key, g => g.Min(a => a.Sequence));
        return units.Join(itemRows, u => u.UnitId, a => a.UnitId, (u, a) => new OrderUnit(u.UnitId, a.ItemId, u.LifecycleStatus,
            sequence[u.UnitId], open.Where(i => i.UnitId == u.UnitId).ToList(), Flags(u))).OrderBy(u => u.AllocationSequence).ToList();
    }

    private async Task<List<CreationFact>> CreationFactsAsync(DocumentRef productionOrder, CancellationToken cancellationToken)
    {
        // CONTRACT with CommissionUnitsHandler: its creating fact keeps the production-order document, the command id and
        // the commissioning `line` index. Allocation order is (the command's first seq, line). Seq alone is NOT line order:
        // UnitEventWriter orders each append by unit id (see the matching note there). Removing any of these makes plain
        // pasted lists pair with different physical units.
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && e.DocumentType == productionOrder.Type && e.DocumentId == productionOrder.Id
                && (productionOrder.Line == null || e.DocumentLine == productionOrder.Line) && (e.EventType == UnitEventType.Commissioned || e.EventType == UnitEventType.CreatedPendingIdentifier))
            .Select(e => new { e.UnitId, e.Seq, e.Attributes }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && e.DocumentType == productionOrder.Type && e.DocumentId == productionOrder.Id
                && (productionOrder.Line == null || e.DocumentLine == productionOrder.Line) && (e.EventType == UnitEventType.Commissioned || e.EventType == UnitEventType.CreatedPendingIdentifier))
            .Select(e => new { e.UnitId, e.Seq, e.Attributes }).ToListAsync(cancellationToken);
        var facts = live.Concat(archived).Select(e => (e.UnitId, e.Seq, Position: CreationPosition(e.Attributes))).ToList();
        var commandStart = facts.GroupBy(f => f.Position.CommandId ?? f.UnitId).ToDictionary(g => g.Key, g => g.Min(f => f.Seq));
        return facts.OrderBy(f => commandStart[f.Position.CommandId ?? f.UnitId]).ThenBy(f => f.Position.Line ?? 0).ThenBy(f => f.Seq)
            .Select((f, rank) => new CreationFact(f.UnitId, rank)).ToList();
    }

    private static (Guid? CommandId, int? Line) CreationPosition(string? attributes)
    {
        if (attributes is null) return (null, null);
        using var document = JsonDocument.Parse(attributes);
        var root = document.RootElement;
        Guid? command = root.TryGetProperty("command_id", out var c) && c.ValueKind == JsonValueKind.String && Guid.TryParse(c.GetString(), out var g) ? g : null;
        int? line = root.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
        return (command, line);
    }

    private async Task<List<CorrectionFact>> CorrectionFactsAsync(Guid correctionId, CancellationToken cancellationToken)
    {
        var marker = correctionId.ToString("D");
        var unitIds = await db.CorrectionUnits.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.CorrectionId == correctionId)
            .Select(row => row.UnitId).ToListAsync(cancellationToken);
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId)
                && e.EventType == UnitEventType.IdentifierAssigned && e.Attributes != null && e.Attributes.Contains(marker))
            .Select(e => new { e.UnitId, e.DocumentType, e.DocumentId, e.Attributes }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId)
                && e.EventType == UnitEventType.IdentifierAssigned && e.Attributes != null && e.Attributes.Contains(marker))
            .Select(e => new { e.UnitId, e.DocumentType, e.DocumentId, e.Attributes }).ToListAsync(cancellationToken);
        var result = new List<CorrectionFact>();
        foreach (var row in live.Concat(archived))
        {
            using var document = JsonDocument.Parse(row.Attributes!);
            var root = document.RootElement;
            if (!root.TryGetProperty("correction_id", out var correction) || correction.GetGuid() != correctionId) continue;
            var before = root.GetProperty("before");
            var after = root.GetProperty("after");
            result.Add(new(row.UnitId, root.GetProperty("assignment_index").GetInt32(), root.GetProperty("command_id").GetGuid(),
                before.GetProperty("identifier_id").GetGuid(), before.GetProperty("raw_value").GetString()!, before.GetProperty("normalised_value").GetString()!,
                after.GetProperty("identifier_id").GetGuid(), after.GetProperty("raw_value").GetString()!, after.GetProperty("normalised_value").GetString()!,
                row.DocumentType!, row.DocumentId!.Value, root.TryGetProperty("reversal", out var reversed) && reversed.GetBoolean()));
        }

        return result;
    }

    private static UnitIdentifier CloneProvisional(UnitIdentifier source, Guid unitId, AssignIdentifiers command, Guid eventId, DateTime recordedTime) => new()
    {
        TenantId = source.TenantId,
        IdentifierId = TimeOrderedId.New(command.EventTime),
        UnitId = unitId,
        Type = IdentifierType.Provisional,
        Scheme = source.Scheme,
        IssuerPartyRef = source.IssuerPartyRef,
        EnterpriseIdentifier = source.EnterpriseIdentifier,
        Qualifier = source.Qualifier,
        RawValue = source.RawValue,
        NormalisedValue = source.NormalisedValue,
        ScopeKey = source.ScopeKey,
        ValidFrom = command.EventTime,
        ActorRef = command.ActorRef,
        RecordedTime = recordedTime,
        EventId = eventId,
    };

    private static object IdentifierFact(UnitIdentifier identifier) => new
    {
        identifier_id = identifier.IdentifierId,
        type = identifier.Type,
        scheme = identifier.Scheme,
        raw_value = identifier.RawValue,
        normalised_value = identifier.NormalisedValue,
        issuer_party_ref = identifier.IssuerPartyRef,
    };

    private static PublishedCorrection PublishedCorrection(CorrectionMetadata metadata) => new(metadata.CorrectionId, metadata.ReasonCode, metadata.InitiatedVia,
        metadata.CorrectsCorrectionId, metadata.Approvers!.Select(a => a.UserRef).Distinct(StringComparer.Ordinal).ToList());

    private static PlannedLine Rejected(AssignIdentifiersRules.Pair pair, string problem, string? normalised = null, Guid? conflict = null, IReadOnlyList<string>? warnings = null) =>
        new(pair, null, null, problem, warnings ?? []) { Normalised = normalised, ConflictingUnitId = conflict };

    private static bool Eligible(OrderUnit unit) => LifecycleProblem(unit.LifecycleStatus) is null && unit.OpenIdentifiers.Count == 1 && unit.OpenIdentifiers[0].Type == IdentifierType.Provisional;

    private static string? LifecycleProblem(string status) => status switch
    {
        LifecycleStatus.Void => "unit_void",
        LifecycleStatus.Merged => "unit_merged",
        _ => null,
    };

    private static List<string> Flags(Unit unit) => JsonSerializer.Deserialize<List<string>>(unit.OverlayFlags, LedgerJson.Options) ?? [];

    private static void ValidateEnvelope(AssignIdentifiers command)
    {
        if (command.ProductionOrder.Type != ProductionOrderType) throw new CommandRefusedException("production_order_required", $"AssignIdentifiers requires a {ProductionOrderType} document reference.");
        CorrectionRules.ValidateReason(CorrectionCommand.AssignIdentifiers, command.Reason);
    }

    private sealed record OrderUnit(Guid UnitId, Guid ItemId, string LifecycleStatus, long AllocationSequence, IReadOnlyList<UnitIdentifier> OpenIdentifiers, IReadOnlyList<string> Flags);
    private sealed record CreationFact(Guid UnitId, long Sequence);
    private sealed record CorrectionFact(Guid UnitId, int AssignmentIndex, Guid CommandId, Guid BeforeIdentifierId, string BeforeRawValue, string BeforeNormalisedValue,
        Guid AfterIdentifierId, string AfterRawValue, string AfterNormalisedValue, string ProductionOrderType, Guid ProductionOrderId, bool Reversal);
    private sealed class PlannedLine(AssignIdentifiersRules.Pair pair, OrderUnit? unit, PreparedIdentifier? prepared, string? problem, IReadOnlyList<string> warnings)
    {
        public AssignIdentifiersRules.Pair Pair { get; } = pair;
        public OrderUnit? Unit { get; } = unit;
        public PreparedIdentifier? Prepared { get; } = prepared;
        public string? Problem { get; set; } = problem;
        public IReadOnlyList<string> Warnings { get; } = warnings;
        public string? Normalised { get; init; }
        public Guid? ConflictingUnitId { get; set; }
    }
    private sealed record Work(int LineIndex, Guid UnitId, Guid OpenIdentifierId, Guid? RestoreIdentifierId, PreparedIdentifier? Prepared, IReadOnlyList<string> Warnings);
    private sealed record Evaluation(CorrectionMetadata Metadata, IReadOnlyList<IdentifierAssignmentLineResult> Lines, IReadOnlyList<Work> Work,
        IReadOnlyList<Guid> RemainingUnitIds, bool Reversal, CommandRefusal? ApprovalRefusal, IReadOnlyList<OrderUnit> OrderUnits);
}

public static class AssignIdentifiersHandler
{
    public static Task<AssignIdentifiersPreview> Handle(PreviewAssignIdentifiers command, AssignIdentifiersService service, CancellationToken cancellationToken) =>
        service.PreviewAsync(command.Command, cancellationToken);

    public static Task<AssignIdentifiersResult> Handle(AssignIdentifiers command, AssignIdentifiersService service, CancellationToken cancellationToken) =>
        service.ExecuteAsync(command, cancellationToken);
}
